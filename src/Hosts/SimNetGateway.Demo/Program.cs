// Console host (DD-003 section 5): a thin wrapper around the testable DemoRunner in the Showcase assembly, which parses the
// command line (--config / --list / --schema / --bench / --showcase / --help), runs the chosen mode and prints.
using SimNetGateway.Showcase;

return DemoRunner.Run(args, Console.Out, Console.Error);
