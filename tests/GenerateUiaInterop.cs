using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
internal static class GenerateUiaInterop
{
    [DllImport("oleaut32.dll",CharSet=CharSet.Unicode,PreserveSig=false)] private static extern void LoadTypeLibEx(string file,int flags,out ITypeLib lib);
    private sealed class Sink:ITypeLibImporterNotifySink
    { public void ReportEvent(ImporterEventKind kind,int code,string message){Console.WriteLine(message);} public Assembly ResolveRef(object typeLib){throw new NotSupportedException("Unexpected COM dependency");} }
    private static int Main(string[] args)
    {
        ITypeLib lib;LoadTypeLibEx(Environment.GetFolderPath(Environment.SpecialFolder.System)+"\\UIAutomationCore.dll",2,out lib);
        var converter=new TypeLibConverter();var assembly=converter.ConvertTypeLibToAssembly(lib,args[0],TypeLibImporterFlags.ImportAsX64,new Sink(),null,null,"UIAutomationClient",null);
        assembly.Save(System.IO.Path.GetFileName(args[0]));return 0;
    }
}
