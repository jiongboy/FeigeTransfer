<#
.SYNOPSIS
  Retired legacy GitHub publisher.
.DESCRIPTION
  The application's update source now points to the configured Mac mini LAN server.
  Use Publish-LanUpdate.ps1 after packaging is explicitly authorized.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
throw 'GitHub publishing is retired; use Publish-LanUpdate.ps1 for the configured Mac mini server.'
