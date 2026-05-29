const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const CYAN = '\x1b[36m';
const GREEN = '\x1b[32m';
const YELLOW = '\x1b[33m';
const RED = '\x1b[31m';
const NC = '\x1b[0m';

const outDir = path.join(__dirname, './bin/sftp');
const rustProjectDir = path.join(__dirname, './sftp'); // Onde ficará seu código Rust
const rootDir = process.cwd();

if (!fs.existsSync(outDir)) {
    fs.mkdirSync(outDir, { recursive: true });
}

// Resgata as variáveis de versão do GitHub Actions (ou usa mock para dev)
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

for (let i = 0; i < args.length; i++) {
    if (args[i] === '--distro' && i + 1 < args.length) { distroFilter = args[i + 1]; i++; }
    else if (args[i].startsWith('--distro=')) { distroFilter = args[i].split('=')[1]; }
    else if (args[i] === '--csharp') { buildCsharp = true; buildRust = false; if (i + 1 < args.length && !args[i + 1].startsWith('--')) { csprojPath = args[i + 1]; i++; } }
    else if (args[i] === '--only-rust') { buildRust = true; buildCsharp = false; }
    else if (args[i] === '--all') { buildRust = true; buildCsharp = true; }
}

let targetsToBuild = distroFilter ? targets.filter(t => t.id === distroFilter) : targets;

console.log(`${CYAN}===================================================${NC}`);
console.log(`${CYAN}   Iniciando compilação do ecossistema PlumeSFTP${NC}`);
console.log(`${CYAN}   Versão: ${appVersion} | Commit: ${appCommit.substring(0,7)}${NC}`);
console.log(`${CYAN}===================================================${NC}\n`);

// Guarda o último código de erro encontrado (0 significa sucesso)
let exitCode = 0;

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
            
            // Injeção de variáveis via propriedades do MSBuild para preencher o AssemblyMetadata
            let extraArgs = ` -p:AppVersion="${appVersion}" -p:AppCommit="${appCommit}" -p:AppBuildDate="${appBuildDate}" -p:AppBuildTime="${appBuildTime}"`;
            
            // Se for compilação cruzada para Linux ARM64, força o uso do objcopy correto
            if (t.id === 'linux-arm64') {
                extraArgs += ' -p:ObjCopyName=aarch64-linux-gnu-objcopy';
            }

            const dotnetCmd = `dotnet publish "${csprojPath}" -c Release -r ${t.dotnetRid}${extraArgs}`;
            execSync(dotnetCmd, { stdio: 'inherit' });
            console.log(`${GREEN}   [OK] Sucesso (C#): Publicado para ${t.dotnetRid}${NC}`);
        }

    } catch (error) {
        // Pega o status do processo que falhou (se existir), senão joga 1 por padrão
        exitCode = error.status !== undefined ? error.status : 1;
        
        console.error(`${RED}   [ERRO] Falha ao processar ${t.id} (Código: ${exitCode}):${NC}`);
        console.error(error.stderr ? error.stderr.toString() : error.message);
    }
    console.log("");
}

// Se o exitCode mudou durante o loop, encerra passando o código exato do erro
if (exitCode !== 0) {
    console.error(`${RED}===================================================${NC}`);
    console.error(`${RED}   A compilação falhou! Saindo com código: ${exitCode}${NC}`);
    console.error(`${RED}===================================================${NC}`);
    process.exit(exitCode);
}

console.log(`${GREEN}===================================================${NC}`);
console.log(`${GREEN}   Todo o ecossistema foi compilado com sucesso!${NC}`);
console.log(`${GREEN}===================================================${NC}`);