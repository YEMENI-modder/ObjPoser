@echo off
REM Builds ObjPoser.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No Visual Studio needed.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:assets\icon.ico /out:ObjPoser.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\Core.cs src\Skeleton.cs src\Pose.cs src\Skinner.cs src\Export.cs src\Textures.cs src\UI.cs src\ThirdParty\TGASharpLib.cs src\ThirdParty\DDSReaderSharp.cs
if errorlevel 1 (echo Build failed) else (echo Built ObjPoser.exe)
