using System.Net;

namespace SmarterMailMcp.Core.Models;

/// <summary>
/// Exception thrown when a SmarterMail API call fails.
/// Contains the HTTP status code and response body for detailed error reporting.
/// </summary>
public class SmarterMailApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }
    public string Endpoint { get; }

    public SmarterMailApiException(string message, HttpStatusCode statusCode, string responseBody, string endpoint)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Endpoint = endpoint;
    }

    public SmarterMailApiException(string message, HttpStatusCode statusCode, string responseBody, string endpoint, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        Endpoint = endpoint;
    }
}
