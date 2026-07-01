using System;
using Microsoft.Extensions.DependencyInjection;

namespace ApexPharma.Tests.Navigation;

/// <summary>
/// An <see cref="IServiceScopeFactory"/> that wraps a real root provider and records how
/// many scopes it created and disposed. Used to prove the navigation service disposes the
/// PREVIOUS module's scope on every navigation (plan.md §10 — per-visit DbContext
/// lifetime). Each created scope is a real DI scope over the shared provider, so resolving
/// module view-models works exactly as it does in the app.
/// </summary>
public sealed class ProbingScopeFactory : IServiceScopeFactory
{
    private readonly IServiceScopeFactory _inner;

    public ProbingScopeFactory(IServiceProvider rootProvider)
        => _inner = rootProvider.GetRequiredService<IServiceScopeFactory>();

    /// <summary>How many scopes this factory has handed out.</summary>
    public int CreatedCount { get; private set; }

    /// <summary>How many of those scopes have been disposed.</summary>
    public int DisposedCount { get; private set; }

    public IServiceScope CreateScope()
    {
        CreatedCount++;
        return new ProbingScope(_inner.CreateScope(), () => DisposedCount++);
    }

    private sealed class ProbingScope : IServiceScope
    {
        private readonly IServiceScope _inner;
        private readonly Action _onDispose;
        private bool _disposed;

        public ProbingScope(IServiceScope inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
        }

        public IServiceProvider ServiceProvider => _inner.ServiceProvider;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inner.Dispose();
            _onDispose();
        }
    }
}
