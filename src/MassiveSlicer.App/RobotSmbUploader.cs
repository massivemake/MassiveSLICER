using System.Net;
using MassiveSlicer.Core.Models;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace MassiveSlicer.App;

/// <summary>
/// Writes KRL programs straight onto a robot controller's SMB share (the KRC "D drive")
/// without OS-level mounting. Tries SMB2 first, falls back to SMB1 for older KRC2-era
/// controllers. All calls are blocking — run on a background thread.
/// </summary>
internal static class RobotSmbUploader
{
    /// <summary>Connect + login + tree-connect only — the settings "Test" button.</summary>
    public static (bool Ok, string Message) Test(RobotSmbConfig cfg)
    {
        try
        {
            var (client, error) = ConnectAndLogin(cfg);
            if (client is null) return (false, error!);
            try
            {
                var store = client.TreeConnect(cfg.Share.Trim(), out var status);
                if (store is null || status != NTStatus.STATUS_SUCCESS)
                    return (false, $"share \\\\{cfg.Host}\\{cfg.Share} — {status}");
                store.Disconnect();
                return (true, $"Connected to \\\\{cfg.Host}\\{cfg.Share} as {cfg.Username}.");
            }
            finally
            {
                client.Logoff();
                client.Disconnect();
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Uploads <paramref name="content"/> as <paramref name="fileName"/> into the
    /// configured share/folder, overwriting an existing file. Creates the folder if missing.</summary>
    public static (bool Ok, string Message) Upload(RobotSmbConfig cfg, string fileName, byte[] content)
    {
        try
        {
            var (client, error) = ConnectAndLogin(cfg);
            if (client is null) return (false, error!);
            try
            {
                var store = client.TreeConnect(cfg.Share.Trim(), out var status);
                if (store is null || status != NTStatus.STATUS_SUCCESS)
                    return (false, $"share \\\\{cfg.Host}\\{cfg.Share} — {status}");
                try
                {
                    string folder = cfg.Folder.Trim().Trim('\\', '/').Replace('/', '\\');
                    if (folder.Length > 0)
                    {
                        // Ensure the folder chain exists (FILE_OPEN_IF tolerates existing dirs).
                        string partial = "";
                        foreach (var seg in folder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                        {
                            partial = partial.Length == 0 ? seg : $"{partial}\\{seg}";
                            status = store.CreateFile(out var dirHandle, out _, partial,
                                AccessMask.GENERIC_READ, FileAttributes.Directory,
                                ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN_IF,
                                CreateOptions.FILE_DIRECTORY_FILE, null);
                            if (status == NTStatus.STATUS_SUCCESS)
                                store.CloseFile(dirHandle);
                        }
                    }

                    string remotePath = folder.Length > 0 ? $"{folder}\\{fileName}" : fileName;
                    status = store.CreateFile(out var handle, out _, remotePath,
                        AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal,
                        ShareAccess.None, CreateDisposition.FILE_OVERWRITE_IF,
                        CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
                    if (status != NTStatus.STATUS_SUCCESS)
                        return (false, $"create {remotePath} — {status}");

                    try
                    {
                        int max = (int)client.MaxWriteSize;
                        for (int offset = 0; offset < content.Length; offset += max)
                        {
                            int len = Math.Min(max, content.Length - offset);
                            var chunk = new byte[len];
                            Buffer.BlockCopy(content, offset, chunk, 0, len);
                            status = store.WriteFile(out int written, handle, offset, chunk);
                            if (status != NTStatus.STATUS_SUCCESS)
                                return (false, $"write at {offset} — {status}");
                            if (written != len)
                                return (false, $"short write at {offset} ({written}/{len})");
                        }
                    }
                    finally
                    {
                        store.CloseFile(handle);
                    }

                    return (true, $"\\\\{cfg.Host}\\{cfg.Share}\\{remotePath} ({content.Length:N0} bytes)");
                }
                finally
                {
                    store.Disconnect();
                }
            }
            finally
            {
                client.Logoff();
                client.Disconnect();
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (ISMBClient? Client, string? Error) ConnectAndLogin(RobotSmbConfig cfg)
    {
        if (!IPAddress.TryParse(cfg.Host.Trim(), out var ip))
        {
            try
            {
                var addrs = Dns.GetHostAddresses(cfg.Host.Trim());
                if (addrs.Length == 0) return (null, $"host '{cfg.Host}' not found");
                ip = addrs[0];
            }
            catch (Exception ex)
            {
                return (null, $"host '{cfg.Host}' — {ex.Message}");
            }
        }

        // Fast reachability probe — SMBLibrary's own connect can block for minutes on a
        // dead IP (full TCP timeout, twice: SMB2 then SMB1). 4s is generous on a LAN.
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            if (!probe.ConnectAsync(ip, 445).Wait(TimeSpan.FromSeconds(4)))
                return (null, $"could not reach {cfg.Host}:445 within 4s — check the IP and that the controller is on");
        }
        catch (Exception)
        {
            return (null, $"could not reach {cfg.Host}:445 — check the IP and that the controller is on");
        }

        foreach (ISMBClient candidate in new ISMBClient[] { new SMB2Client(), new SMB1Client() })
        {
            if (!candidate.Connect(ip, SMBTransportType.DirectTCPTransport))
                continue;
            var status = candidate.Login(string.Empty, cfg.Username.Trim(), cfg.Password ?? "");
            if (status == NTStatus.STATUS_SUCCESS)
                return (candidate, null);
            candidate.Disconnect();
            if (status is NTStatus.STATUS_LOGON_FAILURE or NTStatus.STATUS_ACCESS_DENIED)
                return (null, $"login as '{cfg.Username}' failed — check username/password");
        }
        return (null, $"could not reach {cfg.Host}:445 (SMB2/SMB1)");
    }
}
