Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
strDir = fso.GetParentFolderName(WScript.ScriptFullName)
WshShell.CurrentDirectory = strDir

strExe = strDir & "\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe"
If Not fso.FileExists(strExe) Then
    strExe = strDir & "\online-bin\TiaPortalSourceBridge.exe"
End If
If Not fso.FileExists(strExe) Then
    strExe = strDir & "\TiaPortalSourceBridge.exe"
End If

strArgs = "web --hide-console --config """ & strDir & "\config.json"""

If WScript.Arguments.Count > 0 Then
    For i = 0 To WScript.Arguments.Count - 1
        strArgs = strArgs & " """ & WScript.Arguments(i) & """"
    Next
End If

WshShell.Run """" & strExe & """ " & strArgs, 0, False
