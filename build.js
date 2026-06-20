const { execSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

// Ajuste os nomes de pasta aqui se estiverem diferentes no seu PC
const RUST_DIR = path.join(__dirname, 'sftp');
const CSHARP_APP_DIR = path.join(__dirname, 'app');
const DOTNET_TARGET = 'net10.0';

console.log("🚀 Iniciando build de desenvolvimento local (Rust -> C#)...");

try {
    // 1. Compila o Rust em modo debug para o seu PC atual
    console.log("📦 Executando 'cargo build'...");
    execSync('cargo build', { cwd: RUST_DIR, stdio: 'inherit' });

    // 2. Determina a extensão do SO para buscar a dynamic lib (cdylib)
    const platform = os.platform();
    let libName = '';

    if (platform === 'win32') {
        libName = 'plume_sftp_core.dll';
    } else if (platform === 'darwin') {
        libName = 'libplume_sftp_core.dylib';
    } else {
        libName = 'libplume_sftp_core.so';
    }

    const sourceLibPath = path.join(RUST_DIR, 'target', 'debug', libName);

    // 3. Valida se o Cargo.toml está configurado corretamente
    if (!fs.existsSync(sourceLibPath)) {
        console.error(`\n❌ Biblioteca gerada não encontrada em: ${sourceLibPath}`);
        console.error('Verifique se [lib] crate-type = ["staticlib", "cdylib"] está no Cargo.toml');
        process.exit(1);
    }

    // 4. Copia a lib pra o C# encontrar sem AOT
    const destDirBin = path.join(CSHARP_APP_DIR, 'bin', 'Debug', DOTNET_TARGET);

    // Garante que a estrutura C# existe
    if (!fs.existsSync(destDirBin)) fs.mkdirSync(destDirBin, { recursive: true });

    // Joga na raiz do projeto e na pasta bin/Debug pra garantir visibilidade do JIT
    fs.copyFileSync(sourceLibPath, path.join(CSHARP_APP_DIR, libName));
    fs.copyFileSync(sourceLibPath, path.join(destDirBin, libName));

    console.log(`\n✅ Sucesso! A lib '${libName}' foi injetada no projeto C#.`);
    console.log(`▶️ Você já pode rodar 'dotnet run' normalmente na pasta 'app/'.`);

} catch (error) {
    console.error("\n❌ Falha catastrófica no build de dev:", error.message);
    process.exit(1);
}