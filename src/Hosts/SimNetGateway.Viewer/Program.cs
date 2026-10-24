// Viewer host (DD-005): a thin wrapper around the testable ViewerProgram, which parses the command line and runs either the
// headless frame loop (--headless --frames N) or the raylib + ImGui window.
using SimNetGateway.Viewer;

return ViewerProgram.Run(args, Console.Out, Console.Error);
