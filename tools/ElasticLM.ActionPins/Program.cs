using System.Text;
using ElasticLM.ActionPins;

// design §20.5 (V1d22): reads workflow files or action definitions as YAML and prints, one per line and tab separated,
// what eng/ci/Test-ActionPins.ps1 checks. The script holds the rules on the values; this tool only reads.
//   workflow <file>...   jobs.<id>.steps[*].uses and jobs.<id>.uses
//   action <file>...     runs.using, runs.image, and runs.steps[*].uses
// Lines:
//   uses <file> <line> <value> <rest of the line after the value> <false|other: with.persist-credentials of the step>
//   job-uses <file> <line> <value>
//   runs <file> <using> <image>
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
