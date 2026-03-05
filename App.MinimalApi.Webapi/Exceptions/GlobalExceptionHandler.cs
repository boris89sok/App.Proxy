namespace App.MinimalApi.Webapi.Exceptions
{
    public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var method = httpContext.Request.Method;
            var path = httpContext.Request.Path;
            var stackTrace = new StackTrace(exception, true);
            var frame = stackTrace.GetFrames()?.FirstOrDefault(f => f.GetFileLineNumber() > 0);
            var lineNumber = frame?.GetFileLineNumber() ?? 0;
            var fileName = (frame?.GetFileName() ?? "UnknownFile").Split("\\").LastOrDefault();
            var inMethod = frame?.GetMethod()?.Name ?? string.Empty;

            using (LogContext.PushProperty("Error", exception, true))
            {
                logger.LogError(exception, @"fileName: {fileName} line: {lineNumber} The following exception was thrown: {Message} method: {inMethod}",
                    fileName, lineNumber, exception.Message, inMethod);
            }

            var exceptionResponse = exception switch
            {
                WebException => BaseResponseDto.ErrorResponse(500),
                UnauthorizedAccessException => BaseResponseDto.ErrorResponse(401),
                ArgumentException => BaseResponseDto.ErrorResponse(400),
                HttpRequestException => BaseResponseDto.ErrorResponse(400),
                BadHttpRequestException bd => BaseResponseDto.ErrorResponse((short)bd.StatusCode),
                ApiException bd => BaseResponseDto.ErrorResponse((short)bd.StatusCode),
                _ => BaseResponseDto.ErrorResponse(500)
            };

            exceptionResponse.Message = exception.Message;
            httpContext.Response.ContentType = "application/json";
            httpContext.Response.StatusCode = (int)(exceptionResponse.Code ?? 500);

            await httpContext.Response.WriteAsJsonAsync(exceptionResponse, cancellationToken);

            return true;
        }
    }
}