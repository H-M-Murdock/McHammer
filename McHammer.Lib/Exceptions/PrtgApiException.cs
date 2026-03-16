namespace McHammer.Lib.Exceptions;

public class PrtgApiException : Exception
{
    public int?   StatusCode    { get; }
    public string? Endpoint     { get; }
    public string? RequestBody  { get; }
    public string? ResponseBody { get; }

    public PrtgApiException(string message) : base(message) { }

    public PrtgApiException(
        string  message,
        int     statusCode,
        string  endpoint,
        string? requestBody  = null,
        string? responseBody = null)
        : base(message)
    {
        StatusCode   = statusCode;
        Endpoint     = endpoint;
        RequestBody  = requestBody;
        ResponseBody = responseBody;
    }
}