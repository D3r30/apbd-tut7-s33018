using ClinicApi.DTOs;
using ClinicApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClinicApi.Controllers;

[ApiController]
[Route("api/appointments")]
public class AppointmentsController : ControllerBase
{
    private readonly IAppointmentService _service;

    public AppointmentsController(IAppointmentService service)
    {
        _service = service;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AppointmentListDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName,
        CancellationToken ct)
    {
        var result = await _service.GetAppointmentsAsync(status, patientLastName, ct);
        return Ok(result);
    }

    [HttpGet("{idAppointment:int}")]
    [ProducesResponseType(typeof(AppointmentDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int idAppointment, CancellationToken ct)
    {
        var result = await _service.GetAppointmentByIdAsync(idAppointment, ct);
        if (result is null)
            return NotFound(new ErrorResponseDto($"Appointment {idAppointment} not found."));
        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(AppointmentDetailsDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateAppointmentRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var created = await _service.CreateAppointmentAsync(request, ct);
            return CreatedAtAction(
                nameof(GetById),
                new { idAppointment = created.IdAppointment },
                created);
        }
        catch (ValidationException ex)
        {
            return BadRequest(new ErrorResponseDto(ex.Message));
        }
        catch (ConflictException ex)
        {
            return Conflict(new ErrorResponseDto(ex.Message));
        }
    }

    [HttpPut("{idAppointment:int}")]
    [ProducesResponseType(typeof(AppointmentDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        int idAppointment,
        [FromBody] UpdateAppointmentRequestDto request,
        CancellationToken ct)
    {
        try
        {
            var updated = await _service.UpdateAppointmentAsync(idAppointment, request, ct);
            return Ok(updated);
        }
        catch (NotFoundException ex)
        {
            return NotFound(new ErrorResponseDto(ex.Message));
        }
        catch (ValidationException ex)
        {
            return BadRequest(new ErrorResponseDto(ex.Message));
        }
        catch (ConflictException ex)
        {
            return Conflict(new ErrorResponseDto(ex.Message));
        }
    }

    [HttpDelete("{idAppointment:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(int idAppointment, CancellationToken ct)
    {
        try
        {
            await _service.DeleteAppointmentAsync(idAppointment, ct);
            return NoContent();
        }
        catch (NotFoundException ex)
        {
            return NotFound(new ErrorResponseDto(ex.Message));
        }
        catch (ConflictException ex)
        {
            return Conflict(new ErrorResponseDto(ex.Message));
        }
    }
}
