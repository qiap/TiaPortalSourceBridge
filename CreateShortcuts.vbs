Option Explicit
' Portable shortcut creator for TIA Portal Source Bridge.
' Resolves every path from this script's own folder, so it works on any machine
' after the package is extracted anywhere. ASCII only on purpose: Windows Script
' Host reads .vbs with the ANSI code page, so non-ASCII text here would garble.

Dim fso, shell, root, exePath, configPath, result, failures

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
configPath = root & "\config.json"

exePath = root & "\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"
If Not fso.FileExists(exePath) Then exePath = root & "\online-bin\TiaPortalSourceBridge.exe"
If Not fso.FileExists(exePath) Then exePath = root & "\TiaPortalSourceBridge.exe"

If Not fso.FileExists(configPath) Then
    Report "config.json was not found. Extract the entire ZIP before creating shortcuts:" & vbCrLf & configPath, 16
    WScript.Quit 1
End If

If Not fso.FileExists(exePath) Then
    Report "TiaPortalSourceBridge.exe was not found." & vbCrLf & vbCrLf & _
           "Build it first (build.bat or build-online.ps1), then run this again.", 16
    WScript.Quit 1
End If

failures = 0
result = ""
If Not WScript.Arguments.Named.Exists("folder-only") Then
    result = MakeLink(shell.SpecialFolders("Desktop") & "\TIA Portal Source Bridge.lnk", "Desktop")
End If
result = result & MakeLink(root & "\TIA Portal Source Bridge.lnk", "Tool folder")
If failures > 0 Then
    Report "Shortcut creation failed:" & vbCrLf & result, 16
    WScript.Quit 1
End If
Report "Shortcuts created:" & vbCrLf & result & vbCrLf & "Target: " & exePath, 64

Sub Report(text, icon)
    If LCase(fso.GetFileName(WScript.FullName)) = "cscript.exe" Then
        WScript.Echo text
    Else
        MsgBox text, icon, "TIA Portal Source Bridge"
    End If
End Sub

Function MakeLink(lnkPath, label)
    Dim sc, args, detail
    On Error Resume Next
    args = "web --hide-console --config " & Chr(34) & configPath & Chr(34)
    Set sc = shell.CreateShortcut(lnkPath)
    sc.TargetPath = exePath
    sc.Arguments = args
    sc.WorkingDirectory = root
    sc.IconLocation = exePath & ",0"
    sc.Description = "TIA Portal Source Bridge Dashboard"
    sc.Save
    detail = Err.Description
    If detail = "" Then
        Set sc = shell.CreateShortcut(lnkPath)
        If StrComp(sc.TargetPath, exePath, vbTextCompare) <> 0 Or sc.Arguments <> args Then detail = "Saved target or arguments differ."
        If Err.Number <> 0 Then detail = Err.Description
    End If
    On Error GoTo 0
    If detail <> "" Then
        failures = failures + 1
        MakeLink = "  [FAILED] " & label & ": " & detail & vbCrLf
    Else
        MakeLink = "  [OK] " & label & " -> " & lnkPath & vbCrLf
    End If
End Function
