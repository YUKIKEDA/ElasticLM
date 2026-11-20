using System.Text;
using ElasticLM.ActionPins;

// design §20.5 (V1d22): reads workflow files or action definitions as YAML, checks the rules that need no network, and
// prints, one per line and tab separated, what eng/ci/Test-ActionPins.ps1 checks against the actions' repositories.
//   workflow <file>...   jobs.<id>.steps[*].uses (jobs.<id>.uses is refused)
//   action <file>...     runs.using, runs.image, and runs.steps[*].uses
// Lines:
//   pin <file> <line> <owner/repo> <folder or empty> <sha> <version>
//   runs <file> <using>
//   error <file> <line> <message>
if (args.Length < 2 || args[0] is not ("workflow" or "action"))
{
    Console.Error.WriteLine("usage: ElasticLM.ActionPins workflow|action <file>...");
    return 2;
}

var output = new StringBuilder();
foreach (string path in args.Skip(1))
{
    ActionFile.Read(args[0] == "action", path, output);
}

Console.Out.Write(output.ToString());
return 0;
