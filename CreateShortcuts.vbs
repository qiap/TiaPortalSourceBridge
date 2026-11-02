Option Explicit
' Portable shortcut creator for TIA Portal Source Bridge.
' Resolves every path from this script's own folder, so it works on any machine
' after the package is extracted anywhere. ASCII only on purpose: Windows Script
' Host reads .vbs with the ANSI code page, so non-ASCII text here would garble.

Dim fso, shell, root, exePath, vbsPath, iconPath, result

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
vbsPath = root & "\LaunchWeb.vbs"

exePath = root & "\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"
If Not fso.FileExists(exePath) Then exePath = root & "\online-bin\TiaPortalSourceBridge.exe"
If Not fso.FileExists(exePath) Then exePath = root & "\TiaPortalSourceBridge.exe"

If Not fso.FileExists(vbsPath) Then
    Report "LaunchWeb.vbs was not found next to this script:" & vbCrLf & vbsPath, 16
    WScript.Quit 1
End If

If Not fso.FileExists(exePath) Then
    Report "TiaPortalSourceBridge.exe was not found." & vbCrLf & vbCrLf & _
           "Build it first (build.bat or build-online.ps1), then run this again.", 16
    WScript.Quit 1
End If

iconPath = root & "\app_v3.ico"
If Not fso.FileExists(iconPath) Then iconPath = root & "\app.ico"
If Not fso.FileExists(iconPath) Then iconPath = exePath

result = ""
result = result & MakeLink(shell.SpecialFolders("Desktop") & "\TIA Portal Source Bridge.lnk", "Desktop")
result = result & MakeLink(root & "\TIA Portal Source Bridge.lnk", "Tool folder")

Report "Shortcuts created:" & vbCrLf & vbCrLf & result & vbCrLf & _
       "Target : wscript.exe" & vbCrLf & _
       "Script : " & vbsPath & vbCrLf & _
       "WorkDir: " & root & vbCrLf & _
       "Icon   : " & iconPath, 64

Sub Report(text, icon)
    If LCase(fso.GetFileName(WScript.FullName)) = "cscript.exe" Then
        WScript.Echo text
    Else
        MsgBox text, icon, "TIA Portal Source Bridge"
    End If
End Sub

Function MakeLink(lnkPath, label)
    Dim sc
    On Error Resume Next
    Set sc = shell.CreateShortcut(lnkPath)
    If Err.Number <> 0 Then
        MakeLink = "  [FAILED] " & label & ": " & Err.Description & vbCrLf
        Err.Clear
        Exit Function
    End If

    sc.TargetPath = shell.ExpandEnvironmentStrings("%SystemRoot%") & "\System32\wscript.exe"
    sc.Arguments = Chr(34) & vbsPath & Chr(34)
    sc.WorkingDirectory = root
    sc.IconLocation = iconPath & ",0"
    sc.Description = "TIA Portal Source Bridge Dashboard"
    sc.Save

    If Err.Number <> 0 Then
        MakeLink = "  [FAILED] " & label & ": " & Err.Description & vbCrLf
        Err.Clear
        Exit Function
    End If

    MakeLink = "  [OK] " & label & " -> " & lnkPath & vbCrLf
    On Error GoTo 0
End Function
