using System;
using System.Linq;
using System.Text;
using CodexAccountSwitcher;

class ProcessProbe
{
    static int Main()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var blockers = ProcessInspector.Inspect();
            Console.WriteLine(ProcessInspector.Describe(blockers));
            Console.WriteLine("Same-session Codex process count: " + blockers.Count);
            Console.WriteLine("VS Code blockers: " + blockers.Count(value => value.Kind == "VSCode"));
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }
    }
}
