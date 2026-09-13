<#
.SYNOPSIS
    下载 ClassIsland 人脸早读考勤插件所需的两个 OpenCV Zoo 模型。

.DESCRIPTION
    模型会保存到项目根目录下的 Assets\Models\，编译时会自动随插件打包。
    也可以直接运行本脚本到任意目录，然后在插件设置页里手动指定模型路径。
#>

[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\Assets\Models')
)

$ErrorActionPreference = 'Stop'

$models = @(
    @{
        Name = 'face_detection_yunet_2023mar.onnx'
        MinSize = 100KB
        Urls = @(
            'https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx'
            'https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx'
            'https://hf-mirror.com/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx'
        )
    },
    @{
        Name = 'face_recognition_sface_2021dec.onnx'
        MinSize = 5MB
        Urls = @(
            'https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx'
            'https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx'
            'https://hf-mirror.com/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx'
        )
    }
)

$resolved = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
New-Item -ItemType Directory -Force -Path $resolved | Out-Null
Write-Host "模型输出目录：$resolved" -ForegroundColor Cyan

foreach ($model in $models) {
    $target = Join-Path $resolved $model.Name

    if ((Test-Path $target) -and ((Get-Item $target).Length -ge $model.MinSize)) {
        Write-Host "[跳过] $($model.Name) 已存在（$([math]::Round((Get-Item $target).Length / 1MB, 2)) MB）" -ForegroundColor Green
        continue
    }

    $done = $false
    foreach ($url in $model.Urls) {
        try {
            Write-Host "[下载] $($model.Name)  <- $url" -ForegroundColor Yellow
            $temp = "$target.part"
            Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing -TimeoutSec 900
            $length = (Get-Item $temp).Length
            if ($length -lt $model.MinSize) {
                Write-Warning "下载到的文件只有 $length 字节，可能是 Git LFS 指针文件，换下一个地址重试。"
                Remove-Item $temp -Force
                continue
            }

            Move-Item -Force $temp $target
            Write-Host "[完成] $($model.Name)（$([math]::Round($length / 1MB, 2)) MB）" -ForegroundColor Green
            $done = $true
            break
        }
        catch {
            Write-Warning "从 $url 下载失败：$($_.Exception.Message)"
        }
    }

    if (-not $done) {
        Write-Error "无法下载 $($model.Name)，请按 README 中的说明手动下载后放到 $resolved"
    }
}

Write-Host "全部完成。" -ForegroundColor Cyan
