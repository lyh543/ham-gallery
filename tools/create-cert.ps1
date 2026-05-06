# 生成本地开发用自签名代码签名证书，导出为 PFX
# 用法: .\tools\create-cert.ps1 [-Password <password>]
param(
    [string]$Password = "dev",
    [string]$Subject = "CN=HamGallery",
    [string]$OutPfx = "$PSScriptRoot\..\FluentGallery\HamGallery.pfx"
)

$OutPfx = [System.IO.Path]::GetFullPath($OutPfx)

# 生成证书（存入当前用户的 My 存储）
$certParams = @{
    Type = "Custom"
    Subject = $Subject
    KeyUsage = "DigitalSignature"
    FriendlyName = "HamGallery Dev Signing"
    CertStoreLocation = "Cert:\CurrentUser\My"
    TextExtension = @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}
$cert = New-SelfSignedCertificate @certParams

# 导出为 PFX
$pwd = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $OutPfx -Password $pwd | Out-Null

# 保存 thumbprint 供 make msix-signed 使用
$thumbprintFile = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($OutPfx), ".cert-thumbprint")
$cert.Thumbprint | Out-File -FilePath $thumbprintFile -Encoding ascii -NoNewline

Write-Host "Certificate created:"
Write-Host "  Subject   : $($cert.Subject)"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  PFX       : $OutPfx"
Write-Host "  Thumbprint file: $thumbprintFile"
Write-Host ""
Write-Host "To trust this cert for local install, run as Administrator:"
Write-Host "  make cert-trust"
