// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Grpc.AspNetCore.Server;
using Grpc.Core;
using Grpc.Shared;
using Grpc.Shared.Server;
using Microsoft.AspNetCore.Grpc.HttpApi.Internal.CallHandlers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Grpc.HttpApi.Internal;

internal sealed class HttpApiServerCallContext : ServerCallContext, IServerCallContextFeature
{
    // TODO(JamesNK): Remove nullable override after Grpc.Core.Api update
    private static readonly AuthContext UnauthenticatedContext = new AuthContext(null!, new Dictionary<string, List<AuthProperty>>());

    private readonly IMethod _method;

    public HttpContext HttpContext { get; }
    public MethodOptions Options { get; }
    public CallHandlerDescriptorInfo DescriptorInfo { get; }
    public bool IsJsonRequestContent { get; }
    public Encoding RequestEncoding { get; }

    internal ILogger Logger { get; }

    private string? _peer;
    private Metadata? _requestHeaders;
    private AuthContext? _authContext;

    public HttpApiServerCallContext(HttpContext httpContext, MethodOptions options, IMethod method, CallHandlerDescriptorInfo descriptorInfo, ILogger logger)
    {
        HttpContext = httpContext;
        Options = options;
        _method = method;
        DescriptorInfo = descriptorInfo;
        Logger = logger;
        IsJsonRequestContent = JsonRequestHelpers.HasJsonContentType(httpContext.Request, out var charset);
        RequestEncoding = JsonRequestHelpers.GetEncodingFromCharset(charset) ?? Encoding.UTF8;

        // Add the HttpContext to UserState so GetHttpContext() continues to work
        HttpContext.Items["__HttpContext"] = httpContext;
    }

    public ServerCallContext ServerCallContext => this;

    protected override string MethodCore => _method.FullName;

    protected override string HostCore => HttpContext.Request.Host.Value;

    protected override string? PeerCore
    {
        get
        {
            // Follows the standard at https://github.com/grpc/grpc/blob/master/doc/naming.md
            if (_peer == null)
            {
                _peer = BuildPeer();
            }

            return _peer;
        }
    }

    private string BuildPeer()
    {
        var connection = HttpContext.Connection;
        if (connection.RemoteIpAddress != null)
        {
            switch (connection.RemoteIpAddress.AddressFamily)
            {
                case AddressFamily.InterNetwork:
                    return $"ipv4:{connection.RemoteIpAddress}:{connection.RemotePort}";
                case AddressFamily.InterNetworkV6:
                    return $"ipv6:[{connection.RemoteIpAddress}]:{connection.RemotePort}";
                default:
                    // TODO(JamesNK) - Test what should be output when used with UDS and named pipes
                    return $"unknown:{connection.RemoteIpAddress}:{connection.RemotePort}";
            }
        }
        else
        {
            return "unknown"; // Match Grpc.Core
        }
    }

    internal async Task ProcessHandlerErrorAsync(Exception ex, string method, bool isStreaming, JsonSerializerOptions options)
    {
        Status status;
        if (ex is RpcException rpcException)
        {
            // RpcException is thrown by client code to modify the status returned from the server.
            // Log the status and detail. Don't log the exception to reduce log verbosity.
            GrpcServerLog.RpcConnectionError(Logger, rpcException.StatusCode, rpcException.Status.Detail);

            status = rpcException.Status;
        }
        else
        {
            GrpcServerLog.ErrorExecutingServiceMethod(Logger, method, ex);

            var message = ErrorMessageHelper.BuildErrorMessage("Exception was thrown by handler.", ex, Options.EnableDetailedErrors);

            // Note that the exception given to status won't be returned to the client.
            // It is still useful to set in case an interceptor accesses the status on the server.
            status = new Status(StatusCode.Unknown, message, ex);
        }

        await JsonRequestHelpers.SendErrorResponse(HttpContext.Response, RequestEncoding, status, options);
        if (isStreaming)
        {
            await HttpContext.Response.Body.WriteAsync(GrpcProtocolConstants.StreamingDelimiter);
        }
    }

    // Deadline returns max value when there isn't a deadline.
    protected override DateTime DeadlineCore => DateTime.MaxValue;

    protected override Metadata RequestHeadersCore
    {
        get
        {
            if (_requestHeaders == null)
            {
                _requestHeaders = new Metadata();

                foreach (var header in HttpContext.Request.Headers)
                {
                    // gRPC metadata contains a subset of the request headers
                    // Filter out pseudo headers (start with :) and other known headers
                    if (header.Key.StartsWith(':') || GrpcProtocolConstants.FilteredHeaders.Contains(header.Key))
                    {
                        continue;
                    }
                    else if (header.Key.EndsWith(Metadata.BinaryHeaderSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        _requestHeaders.Add(header.Key, GrpcProtocolHelpers.ParseBinaryHeader(header.Value!));
                    }
                    else
                    {
                        _requestHeaders.Add(header.Key, header.Value);
                    }
                }
            }

            return _requestHeaders;
        }
    }

    protected override CancellationToken CancellationTokenCore => HttpContext.RequestAborted;

    protected override Metadata ResponseTrailersCore => throw new NotImplementedException();

    protected override Status StatusCore { get; set; }

    protected override WriteOptions WriteOptionsCore
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    protected override AuthContext AuthContextCore
    {
        get
        {
            if (_authContext == null)
            {
                var clientCertificate = HttpContext.Connection.ClientCertificate;

                _authContext = clientCertificate == null
                    ? UnauthenticatedContext
                    : AuthContextHelpers.CreateAuthContext(clientCertificate);
            }

            return _authContext;
        }
    }

    protected override IDictionary<object, object?> UserStateCore => HttpContext.Items;

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options)
    {
        throw new NotImplementedException();
    }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        // Headers can only be written once. Throw on subsequent call to write response header instead of silent no-op.
        if (HttpContext.Response.HasStarted)
        {
            throw new InvalidOperationException("Response headers can only be sent once per call.");
        }

        if (responseHeaders != null)
        {
            foreach (var entry in responseHeaders)
            {
                if (entry.IsBinary)
                {
                    HttpContext.Response.Headers[entry.Key] = Convert.ToBase64String(entry.ValueBytes);
                }
                else
                {
                    HttpContext.Response.Headers[entry.Key] = entry.Value;
                }
            }
        }

        return HttpContext.Response.BodyWriter.FlushAsync().GetAsTask();
    }
}