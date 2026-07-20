Option Explicit

Dim arguments, fileSystem, launcherPath, powerShellPath, shell, command
Set arguments = WScript.Arguments
If arguments.Count <> 4 Then
    MsgBox "The TechBench Server Manager shortcut is incomplete. Reinstall the TechBench Sync Service.", vbCritical, "TechBench Server Manager"
    WScript.Quit 1
End If

Set fileSystem = CreateObject("Scripting.FileSystemObject")
launcherPath = fileSystem.BuildPath(fileSystem.GetParentFolderName(WScript.ScriptFullName), "Start-TechBenchServerManager.ps1")
If Not fileSystem.FileExists(launcherPath) Then
    MsgBox "The TechBench Server Manager launcher is missing:" & vbCrLf & launcherPath & vbCrLf & vbCrLf & "Reinstall the TechBench Sync Service.", vbCritical, "TechBench Server Manager"
    WScript.Quit 1
End If

powerShellPath = fileSystem.BuildPath(fileSystem.GetSpecialFolder(1), "WindowsPowerShell\v1.0\powershell.exe")
If Not fileSystem.FileExists(powerShellPath) Then
    MsgBox "64-bit Windows PowerShell 5.1 was not found:" & vbCrLf & powerShellPath, vbCritical, "TechBench Server Manager"
    WScript.Quit 1
End If

Set shell = CreateObject("WScript.Shell")
command = Quote(powerShellPath) & _
    " -NoLogo -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File " & Quote(launcherPath) & _
    " -ServiceName " & Quote(arguments(0)) & _
    " -InstallDirectory " & Quote(arguments(1)) & _
    " -DataDirectory " & Quote(arguments(2)) & _
    " -ManagerDirectory " & Quote(arguments(3))

On Error Resume Next
Call shell.Run(command, 0, False)
If Err.Number <> 0 Then
    MsgBox "TechBench Server Manager could not launch." & vbCrLf & vbCrLf & Err.Description, vbCritical, "TechBench Server Manager"
    WScript.Quit 1
End If
On Error GoTo 0
WScript.Quit 0

Function Quote(ByVal value)
    Quote = Chr(34) & Replace(CStr(value), Chr(34), Chr(34) & Chr(34)) & Chr(34)
End Function
