using System.Runtime.InteropServices;
using GitCredentialManager;
using GitCredentialManager.Interop.Linux;
using GitCredentialManager.Interop.MacOS;
using GitCredentialManager.Interop.Windows;

namespace Layeron.Plugins.Agent;

/// <summary>
/// The agent's durable key store: the OS-level credential facility, the same arrangement
/// <c>sqliteFAot</c>'s key storage uses. Windows encrypts a file with DPAPI (per user, so a copy
/// taken from another account or machine is unreadable), macOS puts the secret in the login
/// keychain and Linux asks the desktop's Secret Service.
/// <para>
/// The interop stores are instantiated directly rather than through Git Credential Manager's static
/// factory: that factory builds a full command context - settings, terminals, git lookups - none of
/// which a key store needs. Only the three store implementations are used, and only one of them is
/// ever constructed, on the platform the process is running on.
/// </para>
/// <para>
/// Every operation is best-effort: a store that cannot be created - a headless Linux session without
/// a secret service, a locked-down process - costs durability, not function. The options layer treats
/// an empty answer from here exactly like an unset key and falls back to the environment variable.
/// </para>
/// </summary>
internal sealed class SecureApiKeyStore
{
    /// <summary>The credential's service name, namespaced so it cannot collide with git's own entries.</summary>
    public const string ServiceName = "layeron.agent";

    /// <summary>The credential's account name: the agent keeps one key per machine, not one per endpoint.</summary>
    public const string AccountName = "api-key";

    /// <summary>GCM namespace that scopes the store's own layout (its directories and attributes).</summary>
    private const string StoreNamespace = "layeron";

    private readonly ICredentialStore? _store;

    /// <summary>
    /// Creates the store. <paramref name="storeRoot"/> moves the Windows file store - where the
    /// encrypted files live - which is how tests point it at a scratch directory instead of the
    /// user's real one. Other platforms keep their system stores and ignore the argument.
    /// </summary>
    public SecureApiKeyStore(string? storeRoot = null)
    {
        try
        {
            _store = CreateStore(storeRoot);
        }
        catch (Exception)
        {
            // An unsupported or failing platform means no durability: the typed key still applies
            // for the session, which is what the options layer assumed before this store existed.
            _store = null;
        }
    }

    /// <summary>Whether a credential facility is usable on this machine and session.</summary>
    public bool IsAvailable => _store is not null;

    /// <summary>Where the Windows file store lives when no explicit root is given.</summary>
    private static string DefaultWindowsRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Layeron", "credentials");

    /// <summary>The same platform selection the reference implementation makes, minus the factory.</summary>
    private static ICredentialStore CreateStore(string? storeRoot)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DpapiCredentialStore(
                new WindowsFileSystem(),
                string.IsNullOrWhiteSpace(storeRoot) ? DefaultWindowsRoot() : storeRoot,
                StoreNamespace);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOSKeychain(StoreNamespace);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new SecretServiceCollection(StoreNamespace);

        throw new PlatformNotSupportedException();
    }

    /// <summary>The stored key, or null when none was saved or the store could not be read.</summary>
    public string? Load()
    {
        try
        {
            return _store?.Get(ServiceName, AccountName)?.Password;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Saves or replaces the key. False when the store is unavailable or refused the write.</summary>
    public bool Save(string secret)
    {
        try
        {
            _store?.AddOrUpdate(ServiceName, AccountName, secret);
            return _store is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Removes the stored key. False when there was nothing to remove or the store is unavailable.</summary>
    public bool Delete()
    {
        try
        {
            return _store?.Remove(ServiceName, AccountName) ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
