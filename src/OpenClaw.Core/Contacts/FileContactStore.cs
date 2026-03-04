using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Contacts;

public sealed class FileContactStore : IContactStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileContactStore(string basePath)
    {
        Directory.CreateDirectory(basePath);
        _path = Path.Combine(basePath, "contacts.json");
    }

    public async ValueTask<Contact?> GetAsync(string phoneE164, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadUnsafeAsync(ct);
            state.ContactsByPhone.TryGetValue(phoneE164, out var contact);
            return contact;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Contact> TouchAsync(string phoneE164, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadUnsafeAsync(ct);
            if (!state.ContactsByPhone.TryGetValue(phoneE164, out var contact))
            {
                contact = new Contact { PhoneE164 = phoneE164 };
                state.ContactsByPhone[phoneE164] = contact;
            }

            contact.LastSeenAt = DateTimeOffset.UtcNow;
            await SaveUnsafeAsync(state, ct);
            return contact;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SetDoNotTextAsync(string phoneE164, bool doNotText, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await LoadUnsafeAsync(ct);
            if (!state.ContactsByPhone.TryGetValue(phoneE164, out var contact))
            {
                contact = new Contact { PhoneE164 = phoneE164 };
                state.ContactsByPhone[phoneE164] = contact;
            }

            contact.DoNotText = doNotText;
            contact.LastSeenAt = DateTimeOffset.UtcNow;
            await SaveUnsafeAsync(state, ct);
            return;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ContactStoreState> LoadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return new ContactStoreState();
        try
        {
            await using var stream = new FileStream(_path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

            return await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.ContactStoreState, ct)
                ?? new ContactStoreState();
        }
        catch (FileNotFoundException)
        {
            return new ContactStoreState();
        }
        catch (DirectoryNotFoundException)
        {
            return new ContactStoreState();
        }
        catch (JsonException)
        {
            return new ContactStoreState();
        }
    }

    private async ValueTask SaveUnsafeAsync(ContactStoreState state, CancellationToken ct)
    {
        var tmp = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            }))
            {
                await JsonSerializer.SerializeAsync(stream, state, CoreJsonContext.Default.ContactStoreState, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
