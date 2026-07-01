using System;
using System.Threading.Tasks;
using ApexPharma.Desktop.Navigation;
using ApexPharma.Domain.Enums;

namespace ApexPharma.Tests.Navigation;

/// <summary>
/// A test view-model whose <see cref="ActivateAsync"/> completion is externally controlled,
/// so a test can start a navigation, hold it mid-activation, begin a second navigation, then
/// release the first — reproducing the re-entrancy race the navigation guard must win. It
/// can also be told to fault, exercising the non-fatal activation-failure path.
/// </summary>
public sealed class ControllableActivatableViewModel : IActivatableViewModel
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _fault;

    /// <param name="completeImmediately">
    /// When true, activation completes as soon as it is awaited (a fast navigation). When
    /// false, activation blocks until <see cref="Release"/> is called (a slow navigation).
    /// </param>
    /// <param name="fault">When true, activation throws instead of completing.</param>
    public ControllableActivatableViewModel(bool completeImmediately, bool fault = false)
    {
        _fault = fault;
        if (completeImmediately)
        {
            Release();
        }
    }

    /// <summary>True once <see cref="ActivateAsync"/> has been entered by the navigation service.</summary>
    public bool ActivationStarted { get; private set; }

    /// <summary>Unblocks a slow activation so it runs to completion (or faults).</summary>
    public void Release() => _gate.TrySetResult();

    public async Task ActivateAsync(UserRole role)
    {
        ActivationStarted = true;
        await _gate.Task;
        if (_fault)
        {
            throw new InvalidOperationException("Simulated activation failure (e.g. a DB error).");
        }
    }
}
