Roslyn
======
 
Mono compatible copy of Roslyn Source Code

The build requires master mono c5cf618a14711a2144b8a017ad0b61693253bd9c or newer
 
C# compiler
============

The compiler can be build using xbuild as `xbuild Src/Compilers/CSharp/csc/csc.csproj'
 
Roslyn compiler is called csc.exe and when built with make it can be found in top level Binaries folder

Workspaces
===========

Use `xbuild Src/Workspaces/CSharp/CSharpWorkspace.csproj' to build C# workspace


Manual changes needed
======================

After succesfull package restore `packages/Microsoft.Net.ToolsetCompilers.0.7.4092303-beta/build/Microsoft.Net.ToolsetCompilers.props' has to be replaced with
```xml
<Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <DisableRoslyn>true</DisableRoslyn>
    <CscToolPath Condition=" '$(OS)' == 'Windows_NT'">$(MSBuildThisFileDirectory)..\tools</CscToolPath>
    <CscToolExe Condition=" '$(OS)' == 'Windows_NT'">csc2.exe</CscToolExe>
    <VbcToolPath>$(MSBuildThisFileDirectory)..\tools</VbcToolPath>
    <VbcToolExe>vbc2.exe</VbcToolExe>
  </PropertyGroup>
</Project>

