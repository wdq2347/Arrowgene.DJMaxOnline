using Arrowgene.DJMaxOnline.Updater;

namespace Arrowgene.DJMaxOnline.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            // Publishing a patch means hashing the folder you are about to upload, so the
            // launcher doubles as the tool that writes the checksum list.
            int manifest = Array.FindIndex(args, argument =>
                string.Equals(argument, "--make-manifest", StringComparison.OrdinalIgnoreCase));
            if (manifest >= 0)
            {
                MakeManifest(args, manifest);
                return;
            }

            Application.Run(new LauncherForm(LauncherStartupOptions.Parse(args)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "DJMAX Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void MakeManifest(IReadOnlyList<string> args, int index)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException("--make-manifest requires a folder.");
        }

        string folder = Path.GetFullPath(args[index + 1]);
        UpdateManifest manifest = UpdateManifest.Create(folder);
        string output = Path.Combine(folder, UpdateManifest.DefaultFileName);
        File.WriteAllText(output, manifest.Write());

        MessageBox.Show(
            $"Wrote {manifest.Entries.Count} entr{(manifest.Entries.Count == 1 ? "y" : "ies")} to:\n\n{output}",
            "DJMAX Launcher",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}

internal sealed record LauncherStartupOptions(
    string? AccountId,
    string? GamePath,
    string? LoginUrl,
    string? UpdateSource)
{
    public static LauncherStartupOptions Parse(IReadOnlyList<string> args)
    {
        string? accountId = ResolveOption(args, "--account");
        string? gamePath = ResolveOption(args, "--game") ??
                           Environment.GetEnvironmentVariable("DJMAX_GAME");
        string? loginUrl = ResolveOption(args, "--login") ??
                           Environment.GetEnvironmentVariable("DJMAX_LOGIN_URL");
        string? updateSource = ResolveOption(args, "--update") ??
                               Environment.GetEnvironmentVariable("DJMAX_UPDATE_URL");
        return new LauncherStartupOptions(accountId, gamePath, loginUrl, updateSource);
    }

    private static string? ResolveOption(IReadOnlyList<string> args, string option)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (++index >= args.Count)
            {
                throw new ArgumentException($"{option} requires a value.");
            }
            return args[index];
        }
        return null;
    }
}
