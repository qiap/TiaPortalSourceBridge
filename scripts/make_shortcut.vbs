Set WshShell = CreateObject("WScript.Shell")

desktop = WshShell.SpecialFolders("Desktop")
vbsPath = "E:\WORK\TiaPortalSourceBridge\LaunchWeb.vbs"
iconPath = "E:\WORK\TiaPortalSourceBridge\app_v3.ico,0"

Set sc1 = WshShell.CreateShortcut(desktop & "\TIA Portal Source Bridge.lnk")
sc1.TargetPath = "C:\Windows\System32\wscript.exe"
sc1.Arguments = """" & vbsPath & """"
sc1.WorkingDirectory = "E:\WORK\TiaPortalSourceBridge"
sc1.IconLocation = iconPath
sc1.Description = "TIA Portal Source Bridge Dashboard"
sc1.Save

Set sc2 = WshShell.CreateShortcut("E:\WORK\TiaPortalSourceBridge\TIA Portal Source Bridge.lnk")
sc2.TargetPath = "C:\Windows\System32\wscript.exe"
sc2.Arguments = """" & vbsPath & """"
sc2.WorkingDirectory = "E:\WORK\TiaPortalSourceBridge"
sc2.IconLocation = iconPath
sc2.Description = "TIA Portal Source Bridge Dashboard"
sc2.Save

WScript.Echo "Shortcuts created successfully."
