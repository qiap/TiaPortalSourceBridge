Option Explicit
Dim fs, root, exe, args, testMode
Set fs = CreateObject("Scripting.FileSystemObject")
root = fs.GetParentFolderName(WScript.ScriptFullName)
exe = root & "\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"
If Not fs.FileExists(exe) Then exe = root & "\online-bin\TiaPortalSourceBridge.exe"
If Not fs.FileExists(exe) Then exe = root & "\TiaPortalSourceBridge.exe"
If Not fs.FileExists(exe) Then
    WScript.Echo "Dashboard EXE missing. Extract the entire ZIP first."
    WScript.Quit 1
End If
testMode = WScript.Arguments.Named.Exists("folder-only")
args = "create-shortcuts --config " & Chr(34) & root & "\config.json" & Chr(34)
If testMode Then
    args = args & " --folder-only"
Else
    args = args & " --notify"
End If
CreateObject("Shell.Application").ShellExecute exe, args, root, "open", 0
