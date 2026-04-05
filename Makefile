PROJ        = FluentGallery\FluentGallery.csproj
TEST_PROJ   = FluentGallery.Tests\FluentGallery.Tests.csproj
EXE         = FluentGallery\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentGallery.exe
RELEASE_BIN = FluentGallery\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64
RELEASE_DIR ?= publish

# Environment: defaults to dev (DEV_BUILD constant, "-Dev" data folder).
# Pass ENV=prod to build the production variant (no suffix).
#   make build             → development build  (default)
#   make build ENV=prod    → production build
#   make build-run ENV=prod → build + run (prod)
ENV ?= dev
ifeq ($(ENV),prod)
  ENV_FLAG =
else
  ENV_FLAG = -p:DevBuild=true
endif

INSTALL_DIR ?= C:\Tools\FluentGallery

.DEFAULT_GOAL := release-prod
.PHONY: build run watch build-run test-all test help kill release release-prod install

PID_FILE = .run.pid
RUN_PS   = powershell -NoProfile -ExecutionPolicy Bypass -File tools/run.ps1 -ExePath $(EXE) -PidFile $(PID_FILE)
KILL_PS  = powershell -NoProfile -ExecutionPolicy Bypass -File tools/kill.ps1 -PidFile $(PID_FILE)

kill:
	-$(KILL_PS)

build: kill
	dotnet build $(PROJ) -p:Platform=x64 $(ENV_FLAG) --runtime win-x64 --no-self-contained -c Debug

run:
	$(RUN_PS)

build-run: build
	$(RUN_PS)

watch:
	dotnet watch run --no-hot-reload --project $(PROJ) -p:Platform=x64 $(ENV_FLAG) --runtime win-x64 --no-self-contained -c Debug

## make / make release     — Release 构建（ENV=prod，输出到 bin\x64\Release\...）
## make install            — 复制已构建的文件到 INSTALL_DIR（默认 C:\Tools\FluentGallery）

release-prod:
	$(MAKE) release ENV=prod

release:
	dotnet build $(PROJ) -p:Platform=x64 -c Release --runtime win-x64 --no-self-contained $(ENV_FLAG)

install:
	powershell -NoProfile -Command "robocopy '$(RELEASE_BIN)' '$(INSTALL_DIR)' /MIR /IS /IT /NJH /NFL /NDL /NP; if ($$LASTEXITCODE -le 7) { exit 0 } else { exit $$LASTEXITCODE }"
	@echo Installed to: $(INSTALL_DIR)

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
	@echo   make build-run                          Build then run
	@echo   make build-run ENV=prod                 Build (prod) then run
	@echo   make watch                              Watch mode (rebuild+restart on file change)
	@echo   make watch ENV=prod                     Watch mode (prod)
	@echo   make                                    Release build (prod, same as make release ENV=prod)
	@echo   make release ENV=prod                   Release build (bin\x64\Release\...)
	@echo   make install                            Copy release build to INSTALL_DIR (default: C:\Tools\FluentGallery)
	@echo   make install INSTALL_DIR=C:\Apps\Ham    Copy release build to custom dir
	@echo   make test-all                           Run all tests (quiet)
	@echo   make test                               Run all tests (verbose)
	@echo   make test FILTER=X                      Run tests matching name X
