using System;
using System.IO;

public class CommandlineOptions
{
    public static string Folder = string.Empty;        // -f <folder>       (image folder, default is application exe folder)
    public static bool IncludeSubfolders = false;      // -s                (include subfolders)
    public static bool RandomOrder = true;             // -o                (use file order instead of random order)
    public static bool ReshuffleEachCycle = false;     // -x                (reshuffle on each cycle, only applicable if no -o)
    public static bool MaximizeWindow = false;         // -m                (maximize window, full screen)
    public static int IntervalSeconds = 8;             // -i <seconds>      (interval time in seconds, default 8s)

    public const string UsageString =
        "Usage: PhotoSlideshow [-f <folder>] [-s] [-o] [-x] [-m] [-i <seconds>] [-h] [-?]\n" +
        "  -f <folder>\tfolder to use (default is application exe folder)\n" +
        "  -s\t\tinclude subfolders\n" +
        "  -o\t\tuse file order instead of random order\n" +
        "  -x\t\treshuffle on each cycle\n" +
        "  -m\t\tmaximize window\n" +
        "  -i <seconds>\tinterval time in seconds (default 8s)\n" +
        "  -h or -?\t\tshow help screen\n";

    /// <summary>
    /// Parses the command line arguments.
    /// Throws ArgumentException for invalid or missing arguments,
    /// and DirectoryNotFoundException if the folder does not exist.
    /// </summary>
    /// <returns>True if the arguments were parsed successfully, false if help was requested.</returns>
    /// <exception cref="ArgumentException">Thrown for invalid or missing arguments.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown if the folder does not exist.</exception>
    public static bool Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-f":
                    if (++i >= args.Length)
                        throw new ArgumentException("-f requires a folder.");
                    Folder = args[i];
                    break;

                case "-s":
                    IncludeSubfolders = true;
                    break;

                case "-o":
                    RandomOrder = false;
                    break;

                case "-x":
                    ReshuffleEachCycle = true;
                    break;

                case "-m":
                    MaximizeWindow = true;
                    break;

                case "-i":
                    int seconds;
                    if (++i >= args.Length || !int.TryParse(args[i], out seconds) || seconds <= 0)
                        throw new ArgumentException("-i requires a positive whole number of seconds.");
                    IntervalSeconds = seconds;
                    break;

                case "-h":
                case "-?":
                    return false;       // caller shows help message and exits

                default:
                    throw new ArgumentException("Invalid argument: " + args[i]);
            }
        }

        if (!string.IsNullOrWhiteSpace(Folder))
        {
            if (!Directory.Exists(Folder))
                throw new DirectoryNotFoundException("Folder not found: \"" + Folder + "\"");
        }
        
        return true;
    }
}
