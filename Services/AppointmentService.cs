using System.Data;
using ClinicApi.DTOs;
using Microsoft.Data.SqlClient;

namespace ClinicApi.Services;

public interface IAppointmentService
{
    Task<IReadOnlyList<AppointmentListDto>> GetAppointmentsAsync(
        string? status, string? patientLastName, CancellationToken ct);

    Task<AppointmentDetailsDto?> GetAppointmentByIdAsync(int idAppointment, CancellationToken ct);

    Task<AppointmentDetailsDto> CreateAppointmentAsync(
        CreateAppointmentRequestDto request, CancellationToken ct);

    Task<AppointmentDetailsDto> UpdateAppointmentAsync(
        int idAppointment, UpdateAppointmentRequestDto request, CancellationToken ct);

    Task DeleteAppointmentAsync(int idAppointment, CancellationToken ct);
}

public class AppointmentService : IAppointmentService
{
    private readonly string _connectionString;

    public AppointmentService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Missing connection string 'DefaultConnection' in configuration.");
    }

    public async Task<IReadOnlyList<AppointmentListDto>> GetAppointmentsAsync(
        string? status, string? patientLastName, CancellationToken ct)
    {
        const string sql = """
            SELECT
                a.IdAppointment,
                a.AppointmentDate,
                a.Status,
                a.Reason,
                p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value =
            (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value =
            (object?)patientLastName ?? DBNull.Value;

        var result = new List<AppointmentListDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5),
            });
        }
        return result;
    }

    public async Task<AppointmentDetailsDto?> GetAppointmentByIdAsync(
        int idAppointment, CancellationToken ct)
    {
        const string sql = """
            SELECT
                a.IdAppointment, a.AppointmentDate, a.Status, a.Reason,
                a.InternalNotes, a.CreatedAt,
                p.IdPatient, p.FirstName + N' ' + p.LastName AS PatientFullName,
                p.Email, p.PhoneNumber,
                d.IdDoctor, d.FirstName + N' ' + d.LastName AS DoctorFullName,
                d.LicenseNumber,
                s.Name AS Specialization
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
            JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
            WHERE a.IdAppointment = @IdAppointment;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(0),
            AppointmentDate = reader.GetDateTime(1),
            Status = reader.GetString(2),
            Reason = reader.GetString(3),
            InternalNotes = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            IdPatient = reader.GetInt32(6),
            PatientFullName = reader.GetString(7),
            PatientEmail = reader.GetString(8),
            PatientPhoneNumber = reader.GetString(9),
            IdDoctor = reader.GetInt32(10),
            DoctorFullName = reader.GetString(11),
            DoctorLicenseNumber = reader.GetString(12),
            Specialization = reader.GetString(13),
        };
    }

    public async Task<AppointmentDetailsDto> CreateAppointmentAsync(
        CreateAppointmentRequestDto request, CancellationToken ct)
    {
        ValidateReason(request.Reason);
        if (request.AppointmentDate < DateTime.UtcNow)
            throw new ValidationException("Appointment date cannot be in the past.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await EnsurePatientIsActiveAsync(connection, request.IdPatient, ct);
        await EnsureDoctorIsActiveAsync(connection, request.IdDoctor, ct);
        await EnsureNoDoctorConflictAsync(
            connection, request.IdDoctor, request.AppointmentDate,
            idAppointmentToExclude: null, ct);

        const string sql = """
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, N'Scheduled', @Reason);
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;

        var newId = (int)(await command.ExecuteScalarAsync(ct))!;
        return (await GetAppointmentByIdAsync(newId, ct))!;
    }

    public async Task<AppointmentDetailsDto> UpdateAppointmentAsync(
        int idAppointment, UpdateAppointmentRequestDto request, CancellationToken ct)
    {
        ValidateReason(request.Reason);
        if (request.Status is not ("Scheduled" or "Completed" or "Cancelled"))
            throw new ValidationException(
                "Status must be one of: Scheduled, Completed, Cancelled.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        // Получить текущий статус и дату — заодно проверка на 404.
        const string fetchSql = """
            SELECT Status, AppointmentDate
            FROM dbo.Appointments
            WHERE IdAppointment = @IdAppointment;
            """;

        string currentStatus;
        DateTime currentDate;
        await using (var fetch = new SqlCommand(fetchSql, connection))
        {
            fetch.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
            await using var reader = await fetch.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new NotFoundException($"Appointment {idAppointment} not found.");
            currentStatus = reader.GetString(0);
            currentDate = reader.GetDateTime(1);
        }

        if (currentStatus == "Completed" && currentDate != request.AppointmentDate)
            throw new ConflictException(
                "Cannot change the date of a completed appointment.");

        await EnsurePatientIsActiveAsync(connection, request.IdPatient, ct);
        await EnsureDoctorIsActiveAsync(connection, request.IdDoctor, ct);

        if (currentDate != request.AppointmentDate)
        {
            await EnsureNoDoctorConflictAsync(
                connection, request.IdDoctor, request.AppointmentDate,
                idAppointmentToExclude: idAppointment, ct);
        }

        const string updateSql = """
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient,
                IdDoctor = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status = @Status,
                Reason = @Reason,
                InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """;

        await using var update = new SqlCommand(updateSql, connection);
        update.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        update.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        update.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = request.AppointmentDate;
        update.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = request.Status;
        update.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason;
        update.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value =
            (object?)request.InternalNotes ?? DBNull.Value;
        update.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await update.ExecuteNonQueryAsync(ct);

        return (await GetAppointmentByIdAsync(idAppointment, ct))!;
    }

    public async Task DeleteAppointmentAsync(int idAppointment, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string statusSql = """
            SELECT Status
            FROM dbo.Appointments
            WHERE IdAppointment = @IdAppointment;
            """;

        await using var statusCmd = new SqlCommand(statusSql, connection);
        statusCmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        var status = await statusCmd.ExecuteScalarAsync(ct);

        if (status is null)
            throw new NotFoundException($"Appointment {idAppointment} not found.");
        if ((string)status == "Completed")
            throw new ConflictException("Cannot delete a completed appointment.");

        const string deleteSql = """
            DELETE FROM dbo.Appointments
            WHERE IdAppointment = @IdAppointment;
            """;

        await using var delete = new SqlCommand(deleteSql, connection);
        delete.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await delete.ExecuteNonQueryAsync(ct);
    }

    // -------------------- private helpers --------------------

    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ValidationException("Reason cannot be empty.");
        if (reason.Length > 250)
            throw new ValidationException("Reason cannot be longer than 250 characters.");
    }

    private static async Task EnsurePatientIsActiveAsync(
        SqlConnection connection, int idPatient, CancellationToken ct)
    {
        const string sql = """
            SELECT IsActive
            FROM dbo.Patients
            WHERE IdPatient = @IdPatient;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@IdPatient", SqlDbType.Int).Value = idPatient;
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null)
            throw new ValidationException($"Patient {idPatient} does not exist.");
        if ((bool)result == false)
            throw new ValidationException($"Patient {idPatient} is not active.");
    }

    private static async Task EnsureDoctorIsActiveAsync(
        SqlConnection connection, int idDoctor, CancellationToken ct)
    {
        const string sql = """
            SELECT IsActive
            FROM dbo.Doctors
            WHERE IdDoctor = @IdDoctor;
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null)
            throw new ValidationException($"Doctor {idDoctor} does not exist.");
        if ((bool)result == false)
            throw new ValidationException($"Doctor {idDoctor} is not active.");
    }

    private static async Task EnsureNoDoctorConflictAsync(
        SqlConnection connection, int idDoctor, DateTime appointmentDate,
        int? idAppointmentToExclude, CancellationToken ct)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = N'Scheduled'
              AND (@IdAppointment IS NULL OR IdAppointment <> @IdAppointment);
            """;

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        cmd.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;
        cmd.Parameters.Add("@IdAppointment", SqlDbType.Int).Value =
            (object?)idAppointmentToExclude ?? DBNull.Value;

        var count = (int)(await cmd.ExecuteScalarAsync(ct))!;
        if (count > 0)
            throw new ConflictException(
                $"Doctor {idDoctor} already has a scheduled appointment at this time.");
    }
}

public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message) { }
}

public class ConflictException : Exception
{
    public ConflictException(string message) : base(message) { }
}

public class ValidationException : Exception
{
    public ValidationException(string message) : base(message) { }
}
