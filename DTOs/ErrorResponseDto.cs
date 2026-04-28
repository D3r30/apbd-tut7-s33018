namespace ClinicApi.DTOs;

public class ErrorResponseDto
{
    public string Message { get; set; } = string.Empty;
    public string? Detail { get; set; }

    public ErrorResponseDto() { }

    public ErrorResponseDto(string message, string? detail = null)
    {
        Message = message;
        Detail = detail;
    }
}
