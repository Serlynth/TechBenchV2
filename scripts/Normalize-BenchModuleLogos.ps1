param(
    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repositoryPath = [System.IO.Path]::GetFullPath($RepositoryRoot)
$sourceDirectory = Join-Path $repositoryPath "Assets\SourceLogos"
$destinationDirectory = Join-Path $repositoryPath "Assets"
$targetBounds = [System.Drawing.RectangleF]::new(113, 196, 1446, 550)

$logos = @(
    @{
        Name = "csri-techbench-logo.png"
        Bounds = [System.Drawing.RectangleF]::new(113, 179, 1446, 550)
        TargetYAdjustment = 0.5
        TargetWidthAdjustment = 0
        TargetHeightAdjustment = 2
    },
    @{
        Name = "csri-salesbench-logo.png"
        Bounds = [System.Drawing.RectangleF]::new(152, 170, 1385, 520)
        TargetYAdjustment = 0
        TargetWidthAdjustment = 0
        TargetHeightAdjustment = 0
    },
    @{
        Name = "csri-adminbench-logo.png"
        Bounds = [System.Drawing.RectangleF]::new(144, 162, 1416, 531)
        TargetYAdjustment = 0
        TargetWidthAdjustment = -1
        TargetHeightAdjustment = 0
    }
)

foreach ($logo in $logos) {
    $sourcePath = [System.IO.Path]::GetFullPath(
        (Join-Path $sourceDirectory $logo.Name))
    $destinationPath = [System.IO.Path]::GetFullPath(
        (Join-Path $destinationDirectory $logo.Name))
    $temporaryPath = "$destinationPath.normalized.png"

    $sourceIsInsideAssets = $sourcePath.StartsWith(
        $sourceDirectory,
        [System.StringComparison]::OrdinalIgnoreCase)
    $destinationIsInsideAssets = $destinationPath.StartsWith(
        $destinationDirectory,
        [System.StringComparison]::OrdinalIgnoreCase)
    if (-not ($sourceIsInsideAssets -and $destinationIsInsideAssets)) {
        throw "Resolved logo path escaped the expected Assets directory."
    }

    $source = [System.Drawing.Bitmap]::new($sourcePath)
    try {
        $canvas = [System.Drawing.Bitmap]::new(
            $source.Width,
            $source.Height,
            [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($canvas)
            try {
                $graphics.Clear($source.GetPixel(0, 0))
                $graphics.CompositingMode =
                    [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality =
                    [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode =
                    [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode =
                    [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

                $sourceBounds = $logo.Bounds
                $calibratedTarget = [System.Drawing.RectangleF]::new(
                    $targetBounds.X,
                    $targetBounds.Y + $logo.TargetYAdjustment,
                    $targetBounds.Width + $logo.TargetWidthAdjustment,
                    $targetBounds.Height + $logo.TargetHeightAdjustment)
                $scaleX = $calibratedTarget.Width / $sourceBounds.Width
                $scaleY = $calibratedTarget.Height / $sourceBounds.Height
                $destinationRect = [System.Drawing.RectangleF]::new(
                    $calibratedTarget.X - ($sourceBounds.X * $scaleX),
                    $calibratedTarget.Y - ($sourceBounds.Y * $scaleY),
                    $source.Width * $scaleX,
                    $source.Height * $scaleY)

                $graphics.DrawImage($source, $destinationRect)
            }
            finally {
                $graphics.Dispose()
            }

            $canvas.Save(
                $temporaryPath,
                [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $canvas.Dispose()
        }
    }
    finally {
        $source.Dispose()
    }

    Move-Item -LiteralPath $temporaryPath -Destination $destinationPath -Force
    Write-Host "Normalized $($logo.Name)"
}
