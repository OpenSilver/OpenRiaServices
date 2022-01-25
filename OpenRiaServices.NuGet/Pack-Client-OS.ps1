param(
    [string]$Path = ".",
	[string[]]$Include =  @("OpenRiaServices.OpenSilver.Client.Core.nuspec", "OpenRiaServices.OpenSilver.Client.nuspec"),
	[string]$Version = "1.0.0",
	[string]$OpenSilverDependencyVersion = "1.0.0",
	[string]$RepositoryUrl = "https://github.com/OpenSilver/OpenRiaServices",
	[string]$NuGetPath
)

$scriptPath = (Split-Path -Parent $PSCommandPath)
Push-Location $scriptPath

$outputDir = "bin/opensilver"
if (-not (Test-Path $outputDir)) {
    mkdir $outputDir
}

# If NuGet path is not specified then check one folder above git repo, or hope for it to be in the path
if ([string]::IsNullOrEmpty($NuGetPath))
{
	if (Test-Path ..\..\NuGet.exe) { $NuGetPath = "..\..\NuGet.exe"}
	else { $NuGetPath = "nuget.exe"}
}

foreach($nuspec in (dir $Path -Recurse -Include $Include))
{
        echo "Building $nuspec"
        & $NuGetPath pack ($nuspec) -OutputDirectory $outputDir -Properties "PackageVersion=$Version;OpenSilverDependencyVersion=$OpenSilverDependencyVersion;RepositoryUrl=$RepositoryUrl;"
}

Pop-Location