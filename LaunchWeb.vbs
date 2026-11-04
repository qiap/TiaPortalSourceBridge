Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
strDir = fso.GetParentFolderName(WScript.ScriptFullName)
WshShell.CurrentDirectory = strDir

strExe = strDir & "\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalDashboard.exe"
If Not fso.FileExists(strExe) Then
    strExe = strDir & "\online-bin\TiaPortalDashboard.exe"
End If
If Not fso.FileExists(strExe) Then
    strExe = strDir & "\TiaPortalDashboard.exe"
End If

strArgs = "--config """ & strDir & "\config.json"""

If WScript.Arguments.Count > 0 Then
    For i = 0 To WScript.Arguments.Count - 1
        strArgs = strArgs & " """ & WScript.Arguments(i) & """"
    Next
End If

WshShell.Run """" & strExe & """ " & strArgs, 0, False
