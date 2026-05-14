PROJ        = FluentGallery/FluentGallery.csproj
TEST_PROJ   = FluentGallery.Tests/FluentGallery.Tests.csproj
EXE         = FluentGallery\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentGallery.exe

# Architecture: x64 (default), arm64, x86
#   make publish ARCH=arm64
#   make msix-signed ARCH=arm64
ARCH        ?= x64
RID         = win-$(ARCH)
RELEASE_BIN = FluentGallery\bin\$(ARCH)\Release\net10.0-windows10.0.19041.0\$(RID)
RELEASE_OUT = publish\FluentGallery
RELEASE_DIR ?= publish

# Environment flag logic:
#   build / run / watch  — default dev  (pass ENV=prod to override)
#   publish / msix / zip — default prod  (pass ENV=dev to override)
# ENV is unset by default; each group picks its own fallback.
_BUILD_ENV = $(if $(ENV),$(ENV),dev)
_DIST_ENV  = $(if $(ENV),$(ENV),prod)
ENV_FLAG       = $(if $(filter prod,$(_BUILD_ENV)),,-p:DevBuild=true)
DIST_ENV_FLAG  = $(if $(filter prod,$(_DIST_ENV)),,-p:DevBuild=true)

INSTALL_DIR ?= C:\Tools\FluentGallery
VERSION     ?= $(shell powershell -NoProfile -Command "([xml](Get-Content 'FluentGallery/FluentGallery.csproj')).Project.PropertyGroup.InformationalVersion | Select-Object -First 1")

.DEFAULT_GOAL := msix-signed
.PHONY: build run watch test-all test help kill publish install install-publish install-publish install-msix zip msix-unsigned msix-signed cert-create cert-trust clean

PID_FILE = .run.pid
RUN_PS   = powershell -NoProfile -ExecutionPolicy Bypass -File tools/run.ps1 -ExePath $(EXE) -PidFile $(PID_FILE)
KILL_PS  = powershell -NoProfile -ExecutionPolicy Bypass -File tools/kill.ps1 -PidFile $(PID_FILE)

clean:
	dotnet clean $(PROJ) -p:Platform=x64 --runtime win-x64 -c Debug
	dotnet clean $(PROJ) -p:Platform=x64 --runtime win-x64 -c Release

kill:
	-$(KILL_PS)

build: kill
	dotnet build $(PROJ) -p:Platform=x64 $(ENV_FLAG) --runtime win-x64 --no-self-contained -c Debug

run:
	$(RUN_PS)

watch:
	dotnet watch run --no-hot-reload --project $(PROJ) -p:Platform=x64 $(ENV_FLAG) --runtime win-x64 --no-self-contained -c Debug

## make publish [ARCH=...] [ENV=dev]             — 自包含发布（默认 prod）
## make install-publish [ARCH=...]               — 复制已发布文件到 INSTALL_DIR
## make msix-unsigned [ARCH=...] [ENV=dev]       — MSIX 打包（默认 prod，未签名）
## make msix-signed [ARCH=...]                   — 先确保证书，再生成已签名 MSIX
## make install-msix [ARCH=...]                  — 信任证书并执行生成的 Install.ps1


publish:
	dotnet publish $(PROJ) -p:Platform=$(ARCH) -c Release --runtime $(RID) --self-contained $(DIST_ENV_FLAG) -o $(RELEASE_OUT)\$(ARCH)
	powershell -NoProfile -Command "Copy-Item -Path '$(RELEASE_BIN)\FluentGallery.pri' -Destination '$(RELEASE_OUT)\$(ARCH)\FluentGallery.pri' -Force"
	@echo Published to: $(RELEASE_OUT)\$(ARCH)

install-publish:
	powershell -NoProfile -Command "robocopy '$(RELEASE_OUT)\$(ARCH)' '$(INSTALL_DIR)' /MIR /IS /IT /NJH /NFL /NDL /NP; if ($$LASTEXITCODE -le 7) { exit 0 } else { exit $$LASTEXITCODE }"
	@echo
	@echo Installed to: $(INSTALL_DIR)
	@echo Run: $(INSTALL_DIR)\FluentGallery.exe

install: install-msix

zip:
	powershell -NoProfile -ExecutionPolicy Bypass -File tools/zip.ps1 -SourceDir "$(RELEASE_OUT)\$(ARCH)" -DestPath "$(RELEASE_DIR)\FluentGallery-$(VERSION)-portable-$(ARCH).zip"

# MSIX 打包：生成未签名的 .msix 文件。
# 要安装到本机需要先签名（自签证书跑通后安装，或提交 Microsoft Store 自动签名）。
# make cert-create  — 生成自签名证书 FluentGallery/HamGallery.pfx（密码: dev）
# make cert-trust  — 以管理员身份将证书安装到本机受信任人（允许直接安装 MSIX）
MSIX_DIR ?= publish/FluentGallery/msix
MSIX_OUTPUT_DIR = FluentGallery/$(MSIX_DIR)
CERT_PFX = FluentGallery\HamGallery.pfx
CERT_PASSWORD = dev
CERT_THUMBPRINT ?= $(strip $(shell powershell -NoProfile -Command "if (Test-Path 'FluentGallery/.cert-thumbprint') { (Get-Content 'FluentGallery/.cert-thumbprint' -Raw).Trim() }"))
# Optional: pass from CI to embed version in the package
#   make msix-signed MSIX_QUAD_VER=1.0.0.5 MSIX_SEMVER=1.0.0.5
MSIX_QUAD_VER  ?=
MSIX_SEMVER   ?=
_MSIX_VER_FLAGS = $(if $(MSIX_QUAD_VER),-p:AppxPackageVersion=$(MSIX_QUAD_VER)) $(if $(MSIX_SEMVER),-p:InformationalVersion=$(MSIX_SEMVER))
_MSIX_CERT_FLAGS = $(if $(strip $(CERT_THUMBPRINT)),-p:PackageCertificateThumbprint=$(CERT_THUMBPRINT),-p:PackageCertificateKeyFile=$(CERT_PFX) -p:PackageCertificatePassword=$(CERT_PASSWORD))

cert-create:
	py -3 tools/create_cert.py --password "$(CERT_PASSWORD)"

cert-trust:
	pwsh -NoProfile -ExecutionPolicy Bypass -Command "Import-PfxCertificate -FilePath '$(CERT_PFX)' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password (ConvertTo-SecureString '$(CERT_PASSWORD)' -Force -AsPlainText)"
	@echo Done. Certificate trusted on this machine.

## make msix-unsigned [ARCH=...] [ENV=dev]  — MSIX 打包（默认 prod，未签名）
## make msix-unsigned MSIX_QUAD_VER=1.0.0.5 MSIX_SEMVER=1.0.0.5 MSIX_DIR=dist\msix  — CI 模式
msix-unsigned:
	dotnet build $(PROJ) -p:Platform=$(ARCH) -c Release -p:BuildMsix=true $(DIST_ENV_FLAG) "-p:AppxPackageDir=$(MSIX_DIR)/" -p:UapAppxPackageBuildMode=SideloadOnly -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never $(_MSIX_VER_FLAGS) -t:"Build;PrepareForRun"
	@echo
	@echo MSIX output: $(MSIX_OUTPUT_DIR)

## make msix-signed [ARCH=x64|arm64|x86]  — 构建已签名 MSIX（需先 make cert-create）
## make msix-signed CERT_THUMBPRINT=xxx MSIX_QUAD_VER=1.0.0.5 MSIX_SEMVER=1.0.0.5 MSIX_DIR=dist\msix  — CI 模式
msix-signed: cert-create
	dotnet build $(PROJ) -p:Platform=$(ARCH) -c Release -p:BuildMsix=true $(DIST_ENV_FLAG) "-p:AppxPackageDir=$(MSIX_DIR)/" -p:UapAppxPackageBuildMode=SideloadOnly -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never -p:AppxPackageSigningEnabled=true $(_MSIX_CERT_FLAGS) $(_MSIX_VER_FLAGS) -t:"Build;PrepareForRun"
	@echo
	@echo Signed MSIX output: $(MSIX_OUTPUT_DIR)

install-msix: cert-trust
	pwsh -NoProfile -ExecutionPolicy Bypass -Command '$$script = Get-ChildItem -Path ''$(MSIX_OUTPUT_DIR)'' -Filter ''Install.ps1'' -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName; if (-not $$script) { throw ''Install.ps1 not found under $(MSIX_OUTPUT_DIR)'' }; Set-Location (Split-Path $$script -Parent); ./Install.ps1'

## make test-all          — 运行全部测试（安静模式）
## make test              — 运行全部测试（详细输出）
## make test FILTER=Xxx   — 按名称过滤运行单个测试

test-all:
	dotnet test $(TEST_PROJ) -p:Platform=x64 --runtime win-x64 -c Debug

ifdef FILTER
test:
	dotnet test $(TEST_PROJ) -p:Platform=x64 --runtime win-x64 -c Debug --filter "FullyQualifiedName~$(FILTER)"
else
test:
	dotnet test $(TEST_PROJ) -p:Platform=x64 --runtime win-x64 -c Debug --logger "console;verbosity=normal"
endif

help:
	@echo Targets:
	@echo   make build                              Build development (default, DEV_BUILD constant, -Dev data folder)
	@echo   make build ENV=prod                     Build production (no suffix)
	@echo   make run                                Run the built executable
	@echo   make watch                              Watch mode (rebuild+restart on file change)
	@echo   make watch ENV=prod                     Watch mode (prod)
	@echo   make                                    Signed MSIX package (prod, default)
	@echo   make publish [ARCH=x64|arm64|x86]       Self-contained publish (prod, includes .NET runtime)
	@echo   make install-publish [ARCH=x64|arm64|x86]  Copy published output to INSTALL_DIR
	@echo   make install-publish [ARCH=x64|arm64|x86]   Alias of install-publish
	@echo   make publish ENV=dev                    Self-contained publish (dev mode)
	@echo   make msix-unsigned [ARCH=x64|arm64|x86]  MSIX package (prod, unsigned)
	@echo   make msix-signed ENV=dev                Signed MSIX package (dev mode)
	@echo   make msix-signed [ARCH=x64|arm64|x86]   Signed MSIX (prod, needs: make cert-create first)
	@echo   make install                            Alias of install-msix
	@echo   make install-msix [ARCH=x64|arm64|x86]  Trust cert and run generated Install.ps1
	@echo   make zip [ARCH=x64|arm64|x86]           Portable ZIP from published output
	@echo   make test-all                           Run all tests (quiet)
	@echo   make test                               Run all tests (verbose)
	@echo   make test FILTER=X                      Run tests matching name X
	@echo   make clean                              Remove bin and obj directories (Debug + Release)
