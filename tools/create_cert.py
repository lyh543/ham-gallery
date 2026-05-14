import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path


def find_shell() -> str:
    for name in ("pwsh", "powershell"):
        shell_path = shutil.which(name)
        if shell_path:
            return shell_path
    raise RuntimeError(
        "PowerShell is required to generate the signing certificate"
    )


def print_existing(
    subject: str,
    thumbprint: str,
    out_pfx: Path,
    thumbprint_file: Path,
) -> None:
    print("Certificate already exists:")
    print(f"  Subject   : {subject}")
    print(f"  Thumbprint: {thumbprint}")
    print(f"  PFX       : {out_pfx}")
    print(f"  Thumbprint file: {thumbprint_file}")


def create_certificate(password: str, subject: str, out_pfx: Path) -> str:
    shell = find_shell()
    command = r"""
$ErrorActionPreference = 'Stop'
$securePassword = ConvertTo-SecureString `
    -String $env:HG_CERT_PASSWORD `
    -Force `
    -AsPlainText
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $env:HG_CERT_SUBJECT `
    -KeyUsage DigitalSignature `
    -FriendlyName 'HamGallery Dev Signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @(
        '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
        '2.5.29.19={text}'
    )
Export-PfxCertificate `
    -Cert $cert `
    -FilePath $env:HG_CERT_OUTPFX `
    -Password $securePassword | Out-Null
Write-Output $cert.Thumbprint
""".strip()
    env = os.environ.copy()
    env["HG_CERT_PASSWORD"] = password
    env["HG_CERT_SUBJECT"] = subject
    env["HG_CERT_OUTPFX"] = str(out_pfx)
    completed = subprocess.run(
        [shell, "-NoProfile", "-Command", command],
        capture_output=True,
        text=True,
        env=env,
        check=False,
    )
    if completed.returncode != 0:
        sys.stderr.write(completed.stderr)
        raise RuntimeError("Failed to create signing certificate")
    thumbprint = completed.stdout.strip().splitlines()[-1].strip()
    if not thumbprint:
        raise RuntimeError("Certificate thumbprint was not returned")
    return thumbprint


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--password", default="dev")
    parser.add_argument("--subject", default="CN=HamGallery")
    default_out_pfx = (
        Path(__file__).resolve().parents[1]
        / "FluentGallery"
        / "HamGallery.pfx"
    )
    parser.add_argument("--out-pfx", default=str(default_out_pfx))
    args = parser.parse_args()

    out_pfx = Path(args.out_pfx).resolve()
    thumbprint_file = out_pfx.parent / ".cert-thumbprint"

    if out_pfx.exists() and thumbprint_file.exists():
        existing_thumbprint = thumbprint_file.read_text(
            encoding="ascii"
        ).strip()
        print_existing(
            args.subject,
            existing_thumbprint,
            out_pfx,
            thumbprint_file,
        )
        return 0

    out_pfx.parent.mkdir(parents=True, exist_ok=True)
    thumbprint = create_certificate(args.password, args.subject, out_pfx)
    thumbprint_file.write_text(thumbprint, encoding="ascii")

    print("Certificate created:")
    print(f"  Subject   : {args.subject}")
    print(f"  Thumbprint: {thumbprint}")
    print(f"  PFX       : {out_pfx}")
    print(f"  Thumbprint file: {thumbprint_file}")
    print()
    print("To trust this cert for local install, run as Administrator:")
    print("  make cert-trust")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
