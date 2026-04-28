' Hidden launcher shim — invokes PowerShell with 0 window style so Task Scheduler
' triggers don't flash a console frame. Used by ThatsMyQuant-Watchdog (every 1min)
' and ThatsMyQuant-PaperTrade (logon trigger).
'
' Usage:  wscript.exe Run-Hidden.vbs <full-path-to-ps1> [args...]
' Window:  0 = Hidden  (vs PowerShell -WindowStyle Hidden which still flashes briefly)

Set objShell = CreateObject("WScript.Shell")

If WScript.Arguments.Count < 1 Then
    WScript.Quit 1
End If

scriptPath = WScript.Arguments(0)
extraArgs = ""
For i = 1 To WScript.Arguments.Count - 1
    extraArgs = extraArgs & " """ & WScript.Arguments(i) & """"
Next

cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & scriptPath & """" & extraArgs

' Run with window style 0 (hidden) and don't wait for completion.
objShell.Run cmd, 0, False
