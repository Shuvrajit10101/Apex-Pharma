namespace ApexPharma.Application.Services.MasterData;

/// <summary>
/// Outcome of a master-data mutation (create/rename/update/deactivate) — mirrors the
/// <see cref="AuthResult"/> pattern so a validation or authorization failure is a
/// first-class return value, not an exception (plan.md §6.2 fail-with-clear-messages).
/// The generic <see cref="MasterResult{T}"/> also carries the affected entity on success.
/// </summary>
public class MasterResult
{
    protected MasterResult(bool succeeded, string? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    /// <summary>True when the operation validated, was authorized, and persisted.</summary>
    public bool Succeeded { get; }

    /// <summary>Clear, user-facing message describing why the operation failed (null on success).</summary>
    public string? Error { get; }

    public static MasterResult Ok() => new(true, null);

    /// <summary>A validation/authorization failure carrying an explanatory message.</summary>
    public static MasterResult Fail(string error) => new(false, error);
}

/// <summary>
/// A <see cref="MasterResult"/> that also carries the created/updated entity on success.
/// </summary>
/// <typeparam name="T">The entity type returned on success.</typeparam>
public sealed class MasterResult<T> : MasterResult
{
    private MasterResult(bool succeeded, T? value, string? error)
        : base(succeeded, error) => Value = value;

    /// <summary>The affected entity when <see cref="MasterResult.Succeeded"/>; otherwise default.</summary>
    public T? Value { get; }

    public static MasterResult<T> Ok(T value) => new(true, value, null);

    public static new MasterResult<T> Fail(string error) => new(false, default, error);
}
