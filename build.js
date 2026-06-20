const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

const CYAN = '\x1b[36m';
const GREEN = '\x1b[32m';
const YELLOW = '\x1b[33m';
const RED = '\x1b[31m';
const NC = '\x1b[0m';

const outDir = path.join(__dirname, './bin/sftp');
const rustProjectDir = path.join(__dirname, './sftp');
const rootDir = process.cwd();

if (!fs.existsSync(outDir)) {
    fs.mkdirSync(outDir, { recursive: true });
}

// ==========================================
// LOGS DE DEBUG DAS VARIÁVEIS DE AMBIENTE
// ==========================================
const appVersion = process.env.TAG_NAME || 'dev';
const appCommit = process.env.GITHUB_SHA || 'local';
const appBuildTime = process.env.BUILD_TIME || Math.floor(Date.now() / 1000).toString();
const appBuildDate = new Date().toISOString();

// Alvos do Rust (Target Triples)
const targets = [
    { id: 'windows-amd64', rustTarget: 'x86_64-pc-windows-msvc', ext: 'lib', prefix: '', dotnetRid: 'win-x64' },
    { id: 'linux-amd64',   rustTarget: 'x86_64-unknown-linux-gnu', ext: 'a', prefix: 'lib', dotnetRid: 'linux-x64' },
    { id: 'linux-arm64',   rustTarget: 'aarch64-unknown-linux-gnu', ext: 'a', prefix: 'lib', dotnetRid: 'linux-arm64' },
    { id: 'alpine-amd64',  rustTarget: 'x86_64-unknown-linux-musl', ext: 'a', prefix: 'lib', dotnetRid: 'linux-musl-x64' },
    { id: 'mac-arm64',     rustTarget: 'aarch64-apple-darwin', ext: 'a', prefix: 'lib', dotnetRid: 'osx-arm64' }
];

const args = process.argv.slice(2);
let distroFilter = null;
let buildRust = true;
let buildCsharp = false;
let csprojPath = './app/app.csproj';

// Novas variáveis para modo DEV
let isDev = false;
let runCsharp = false;

for (let i = 0; i < args.length; i++) {
    if (args[i] === '--distro' && i + 1 < args.length) { distroFilter = args[i + 1]; i++; }
    else if (args[i].startsWith('--distro=')) { distroFilter = args[i].split('=')[1]; }
    else if (args[i] === '--csharp') { buildCsharp = true; buildRust = false; if (i + 1 < args.length && !args[i + 1].startsWith('--')) { csprojPath = args[i + 1]; i++; } }
    else if (args[i] === '--only-rust') { buildRust = true; buildCsharp = false; }
    else if (args[i] === '--all') { buildRust = true; buildCsharp = true; }
    else if (args[i] === '--dev') { isDev = true; buildRust = true; buildCsharp = true; }
    else if (args[i] === '--run') { runCsharp = true; }
}

let targetsToBuild = distroFilter ? targets.filter(t => t.id === distroFilter) : targets;

console.log(`${CYAN}===================================================${NC}`);
console.log(`${CYAN}   Iniciando compilação do ecossistema PlumeSFTP${NC}`);
console.log(`${CYAN}   Versão: ${appVersion} | Commit: ${appCommit.substring(0,7)}${NC}`);
console.log(`${CYAN}   Modo: ${isDev ? 'DESENVOLVIMENTO (JIT)' : 'PRODUÇÃO (AOT)'}${NC}`);
console.log(`${CYAN}===================================================${NC}\n`);

let exitCode = 0;

// ==========================================
// MODO DESENVOLVIMENTO (LOCAL / JIT)
// ==========================================
if (isDev) {
    try {
        if (buildRust) {
            console.log(`${YELLOW}>> Compilando biblioteca dinâmica (cdylib) em Rust...${NC}`);
            execSync(`cargo build`, { cwd: rustProjectDir, stdio: 'inherit' });

            const platform = os.platform();
            let libName = platform === 'win32' ? 'plume_sftp_core.dll' : (platform === 'darwin' ? 'libplume_sftp_core.dylib' : 'libplume_sftp_core.so');
            const sourceLibPath = path.join(rustProjectDir, 'target', 'debug', libName);

            if (!fs.existsSync(sourceLibPath)) {
                throw new Error(`Biblioteca gerada não encontrada: ${sourceLibPath}. O 'Cargo.toml' tem cdylib?`);
            }

            const destDirBin = path.join(rootDir, 'app', 'bin', 'Debug', 'net10.0');
            if (!fs.existsSync(destDirBin)) fs.mkdirSync(destDirBin, { recursive: true });

            fs.copyFileSync(sourceLibPath, path.join(rootDir, 'app', libName));
            fs.copyFileSync(sourceLibPath, path.join(destDirBin, libName));
            console.log(`${GREEN}   [OK] Sucesso: Lib '${libName}' injetada no C#.${NC}\n`);
        }

        if (buildCsharp) {
            if (runCsharp) {
                console.log(`${YELLOW}>> Executando a aplicação C# (dotnet run)...${NC}`);
                execSync(`dotnet run --project "${csprojPath}"`, { stdio: 'inherit' });
            } else {
                console.log(`${YELLOW}>> Compilando a aplicação C# (dotnet build)...${NC}`);
                execSync(`dotnet build "${csprojPath}"`, { stdio: 'inherit' });
                console.log(`${GREEN}   [OK] C# compilado com sucesso! Use 'node build.js --dev --run' para iniciar.${NC}`);
            }
        }
    } catch (error) {
        exitCode = error.status !== undefined ? error.status : 1;
        console.error(`\n${RED}   [ERRO] Falha no modo DEV (Código: ${exitCode}):${NC}`);
        console.error(error.stderr ? error.stderr.toString() : error.message);
    }
    process.exit(exitCode);
}

// ==========================================
// MODO PRODUÇÃO (NATIVE AOT / CROSS-COMPILE)
// ==========================================
for (const t of targetsToBuild) {
    const finalOutFile = path.join(outDir, `plumesftp-${t.id}.${t.ext}`);
    const rustFileName = `${t.prefix}plume_sftp_core.${t.ext}`;
    const rustOutPath = path.join(rustProjectDir, 'target', t.rustTarget, 'release', rustFileName);

    console.log(`${YELLOW}>> Processando alvo: ${t.id}...${NC}`);

    try {
        if (buildRust) {
            console.log(`   [*] Compilando biblioteca estática em Rust (${t.rustTarget})...`);
            execSync(`rustup target add ${t.rustTarget}`, { stdio: 'pipe' });

            const buildCmd = `cargo build --release --target ${t.rustTarget}`;
            execSync(buildCmd, { cwd: rustProjectDir, stdio: 'inherit' });

            if (fs.existsSync(rustOutPath)) {
                fs.copyFileSync(rustOutPath, finalOutFile);
                console.log(`${GREEN}   [OK] Sucesso (Rust -> Estático): ${finalOutFile}${NC}`);
            } else {
                throw new Error(`Arquivo esperado não encontrado: ${rustOutPath}`);
            }
        }

        if (buildCsharp) {
            console.log(`   [*] Compilando C# NativeAOT para ${t.dotnetRid}...`);
            let extraArgs = ` -p:AppVersion="${appVersion}" -p:AppCommit="${appCommit}" -p:AppBuildDate="${appBuildDate}" -p:AppBuildTime="${appBuildTime}"`;
            if (t.id === 'linux-arm64') extraArgs += ' -p:ObjCopyName=aarch64-linux-gnu-objcopy';

            const dotnetCmd = `dotnet publish "${csprojPath}" -c Release -r ${t.dotnetRid}${extraArgs}`;
            execSync(dotnetCmd, { stdio: 'inherit' });
            console.log(`${GREEN}   [OK] Sucesso (C#): Publicado para ${t.dotnetRid}${NC}`);
        }

    } catch (error) {
        exitCode = error.status !== undefined ? error.status : 1;
        console.error(`${RED}   [ERRO] Falha ao processar ${t.id} (Código: ${exitCode}):${NC}`);
        console.error(error.stderr ? error.stderr.toString() : error.message);
    }
    console.log("");
}

if (exitCode !== 0) {
    console.error(`${RED}===================================================${NC}`);
    console.error(`${RED}   A compilação falhou! Saindo com código: ${exitCode}${NC}`);
    console.error(`${RED}===================================================${NC}`);
    process.exit(exitCode);
}

console.log(`${GREEN}===================================================${NC}`);
console.log(`${GREEN}   Todo o ecossistema foi compilado com sucesso!${NC}`);
console.log(`${GREEN}===================================================${NC}`);