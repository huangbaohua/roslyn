find . -name "obj" | xargs rm -fdr
TOOLSET_DIR=packages/Microsoft.Net.ToolsetCompilers.0.7.4101501-beta/build
mkdir -p $TOOLSET_DIR
echo "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">
  <PropertyGroup>
    <DisableRoslyn>true</DisableRoslyn>
    <CscToolPath Condition=\" '\$(OS)' == 'Windows_NT'\">\$(MSBuildThisFileDirectory)..\tools</CscToolPath>
    <CscToolExe Condition=\" '\$(OS)' == 'Windows_NT'\">csc2.exe</CscToolExe>
    <VbcToolPath>\$(MSBuildThisFileDirectory)..\tools</VbcToolPath>
    <VbcToolExe>vbc2.exe</VbcToolExe>
  </PropertyGroup>
</Project>" >$TOOLSET_DIR/Microsoft.Net.ToolsetCompilers.props
xbuild Src/Tools/Source/FakeSign/FakeSign.csproj
mono Src/.nuget/NuGet.exe restore Src/Roslyn.sln
xbuild /p:Configuration=Debug Src/Compilers/CSharp/csc/csc.csproj
xbuild /p:Configuration=Debug ./Src/Workspaces/CSharp/Desktop/CSharpWorkspace.Desktop.csproj
