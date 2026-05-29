use async_trait::async_trait;
use russh::server::{Auth, Handler, Msg, Session};
use russh::{Channel, ChannelId, CryptoVec};
use std::collections::HashMap;
use std::ffi::{CStr, CString};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::os::raw::{c_char, c_double, c_int};
use std::sync::Arc;
use tokio::net::TcpListener;
use tokio::runtime::Runtime;

// ==========================================
// 1. DEFINIÇÃO DA INTERFACE COM O C# (ABI)
// ==========================================

pub type AuthCallback = extern "C" fn(
    username: *const c_char,
    password: *const c_char,
    server_id: *const c_char,
    out_quota: *mut c_double,
    out_path: *mut c_char,
    max_path_len: c_int,
) -> c_int;

pub type EventCallback = extern "C" fn(event_code: c_int, context_data: *const c_char);

static mut AUTH_CB: Option<AuthCallback> = None;
static mut EVENT_CB: Option<EventCallback> = None;

fn send_event(code: c_int, msg: &str) {
    unsafe {
        if let Some(cb) = EVENT_CB {
            if let Ok(c_msg) = CString::new(msg) {
                cb(code, c_msg.as_ptr());
            }
        }
    }
}

// ==========================================
// 2. UTILITÁRIOS PARA PACOTES SFTP
// ==========================================

struct SftpWriter {
    data: Vec<u8>,
}

impl SftpWriter {
    fn new() -> Self {
        Self { data: vec![0, 0, 0, 0] } // Reserva 4 bytes para o tamanho do pacote
    }
    fn write_u8(&mut self, val: u8) { self.data.push(val); }
    fn write_u32(&mut self, val: u32) { self.data.extend_from_slice(&val.to_be_bytes()); }
    fn write_u64(&mut self, val: u64) { self.data.extend_from_slice(&val.to_be_bytes()); }
    fn write_string(&mut self, s: &str) {
        let bytes = s.as_bytes();
        self.write_u32(bytes.len() as u32);
        self.data.extend_from_slice(bytes);
    }
    fn write_bytes(&mut self, b: &[u8]) {
        self.write_u32(b.len() as u32);
        self.data.extend_from_slice(b);
    }
    fn finish(mut self) -> Vec<u8> {
        let len = (self.data.len() - 4) as u32;
        self.data[0..4].copy_from_slice(&len.to_be_bytes());
        self.data
    }
}

struct SftpReader<'a> {
    data: &'a [u8],
    offset: usize,
}

impl<'a> SftpReader<'a> {
    fn new(data: &'a [u8]) -> Self { Self { data, offset: 0 } }

    fn read_u32(&mut self) -> Option<u32> {
        if self.offset + 4 > self.data.len() { return None; }
        let val = u32::from_be_bytes(self.data[self.offset..self.offset + 4].try_into().unwrap());
        self.offset += 4;
        Some(val)
    }
    fn read_u64(&mut self) -> Option<u64> {
        if self.offset + 8 > self.data.len() { return None; }
        let val = u64::from_be_bytes(self.data[self.offset..self.offset + 8].try_into().unwrap());
        self.offset += 8;
        Some(val)
    }
    fn read_string(&mut self) -> Option<String> {
        let len = self.read_u32()? as usize;
        if self.offset + len > self.data.len() { return None; }
        let s = String::from_utf8_lossy(&self.data[self.offset..self.offset + len]).to_string();
        self.offset += len;
        Some(s)
    }
    fn read_bytes(&mut self) -> Option<&'a [u8]> {
        let len = self.read_u32()? as usize;
        if self.offset + len > self.data.len() { return None; }
        let b = &self.data[self.offset..self.offset + len];
        self.offset += len;
        Some(b)
    }
}

// Funções para responder comandos padrão
fn send_status(channel: ChannelId, session: &mut Session, id: u32, code: u32, msg: &str) {
    let mut w = SftpWriter::new();
    w.write_u8(101); // SSH_FXP_STATUS
    w.write_u32(id);
    w.write_u32(code);
    w.write_string(msg);
    w.write_string("en");
    let mut cv = CryptoVec::new();
    cv.extend(&w.finish());
    let _ = session.data(channel, cv);
}

fn send_handle(channel: ChannelId, session: &mut Session, id: u32, handle: &str) {
    let mut w = SftpWriter::new();
    w.write_u8(102); // SSH_FXP_HANDLE
    w.write_u32(id);
    w.write_string(handle);
    let mut cv = CryptoVec::new();
    cv.extend(&w.finish());
    let _ = session.data(channel, cv);
}

// ==========================================
// 3. O SERVIDOR SSH/SFTP (LÓGICA RUST)
// ==========================================

struct SftpServer {
    client_ip: String,
    root_path: String,
    sftp_buffer: Vec<u8>,
    open_files: HashMap<String, File>,
    open_dirs: HashMap<String, Vec<fs::DirEntry>>,
    next_handle: u64,
}

impl SftpServer {
    fn new(ip: String) -> Self {
        Self {
            client_ip: ip,
            root_path: String::new(),
            sftp_buffer: Vec::new(),
            open_files: HashMap::new(),
            open_dirs: HashMap::new(),
            next_handle: 1,
        }
    }

    // JailedFS: Impede Path Traversal (ex: ../../etc/passwd)
    fn build_safe_path(&self, requested: &str) -> Option<std::path::PathBuf> {
        if requested.contains("..") { return None; }
        let clean_req = requested.trim_start_matches('/');
        let mut path = std::path::PathBuf::from(&self.root_path);
        if !clean_req.is_empty() {
            path.push(clean_req);
        }
        Some(path)
    }

    fn validate_with_csharp(&mut self, user: &str, pass: &str) -> bool {
        let parts: Vec<&str> = user.splitn(2, '_').collect();
        if parts.len() < 2 { return false; }

        let c_user = CString::new(parts[0]).unwrap();
        let c_pass = CString::new(pass).unwrap();
        let c_server = CString::new(parts[1]).unwrap();

        let mut quota: c_double = 0.0;
        let mut path_buf = vec![0u8; 1024];

        unsafe {
            if let Some(cb) = AUTH_CB {
                let result = cb(
                    c_user.as_ptr(), c_pass.as_ptr(), c_server.as_ptr(),
                    &mut quota, path_buf.as_mut_ptr() as *mut c_char, 1024,
                );

                if result == 1 {
                    let c_str = CStr::from_ptr(path_buf.as_ptr() as *const c_char);
                    self.root_path = c_str.to_string_lossy().into_owned();
                    send_event(100, &format!("Acesso concedido para: {}", user));
                    return true;
                }
            }
        }
        false
    }

    // Interpretador nativo do protocolo SFTP v3
    fn handle_sftp_packet(&mut self, channel: ChannelId, packet: &[u8], session: &mut Session) -> Result<(), anyhow::Error> {
        if packet.is_empty() { return Ok(()); }
        let pkt_type = packet[0];

        if pkt_type == 1 { // SSH_FXP_INIT
            let mut w = SftpWriter::new();
            w.write_u8(2); // SSH_FXP_VERSION
            w.write_u32(3); // Version 3
            let mut cv = CryptoVec::new();
            cv.extend(&w.finish());
            let _ = session.data(channel, cv);
            return Ok(());
        }

        let mut reader = SftpReader::new(&packet[1..]);
        let request_id = match reader.read_u32() {
            Some(id) => id,
            None => return Ok(()),
        };

        match pkt_type {
            3 => { // SSH_FXP_OPEN
                let path = reader.read_string().unwrap_or_default();
                let pflags = reader.read_u32().unwrap_or(0);

                if let Some(safe_path) = self.build_safe_path(&path) {
                    let mut options = OpenOptions::new();
                    if pflags & 0x00000001 != 0 { options.read(true); }
                    if pflags & 0x00000002 != 0 { options.write(true); }
                    if pflags & 0x00000004 != 0 { options.append(true); }
                    if pflags & 0x00000008 != 0 { options.create(true); }
                    if pflags & 0x00000010 != 0 { options.truncate(true); }
                    if pflags & 0x00000020 != 0 { options.create_new(true); }

                    match options.open(&safe_path) {
                        Ok(file) => {
                            let handle = format!("FILE:{}", self.next_handle);
                            self.next_handle += 1;
                            self.open_files.insert(handle.clone(), file);
                            send_handle(channel, session, request_id, &handle);
                        }
                        Err(e) => send_status(channel, session, request_id, 4, &e.to_string()),
                    }
                } else {
                    send_status(channel, session, request_id, 3, "Acesso Negado");
                }
            }
            4 => { // SSH_FXP_CLOSE
                let handle = reader.read_string().unwrap_or_default();
                if self.open_files.remove(&handle).is_some() || self.open_dirs.remove(&handle).is_some() {
                    send_status(channel, session, request_id, 0, "OK");
                } else {
                    send_status(channel, session, request_id, 2, "Handle Inválido");
                }
            }
            5 => { // SSH_FXP_READ
                let handle = reader.read_string().unwrap_or_default();
                let offset = reader.read_u64().unwrap_or(0);
                let len = reader.read_u32().unwrap_or(0) as usize;

                if let Some(file) = self.open_files.get_mut(&handle) {
                    if file.seek(SeekFrom::Start(offset)).is_ok() {
                        let read_len = std::cmp::min(len, 64 * 1024); // Evita limite de memória
                        let mut buf = vec![0u8; read_len];
                        match file.read(&mut buf) {
                            Ok(0) => send_status(channel, session, request_id, 1, "EOF"),
                            Ok(n) => {
                                buf.truncate(n);
                                let mut w = SftpWriter::new();
                                w.write_u8(103); // DATA
                                w.write_u32(request_id);
                                w.write_bytes(&buf);
                                let mut cv = CryptoVec::new();
                                cv.extend(&w.finish());
                                let _ = session.data(channel, cv);
                            }
                            Err(e) => send_status(channel, session, request_id, 4, &e.to_string()),
                        }
                    } else {
                        send_status(channel, session, request_id, 4, "Falha de Leitura");
                    }
                } else {
                    send_status(channel, session, request_id, 2, "Handle Inválido");
                }
            }
            6 => { // SSH_FXP_WRITE
                let handle = reader.read_string().unwrap_or_default();
                let offset = reader.read_u64().unwrap_or(0);
                let data = reader.read_bytes().unwrap_or(&[]);

                if let Some(file) = self.open_files.get_mut(&handle) {
                    if file.seek(SeekFrom::Start(offset)).is_ok() && file.write_all(data).is_ok() {
                        send_status(channel, session, request_id, 0, "OK");
                    } else {
                        send_status(channel, session, request_id, 4, "Falha na Escrita");
                    }
                } else {
                    send_status(channel, session, request_id, 2, "Handle Inválido");
                }
            }
            11 => { // SSH_FXP_OPENDIR
                let path = reader.read_string().unwrap_or_default();
                if let Some(safe_path) = self.build_safe_path(&path) {
                    if let Ok(rd) = fs::read_dir(&safe_path) {
                        let entries: Vec<_> = rd.filter_map(Result::ok).collect();
                        let handle = format!("DIR:{}", self.next_handle);
                        self.next_handle += 1;
                        self.open_dirs.insert(handle.clone(), entries);
                        send_handle(channel, session, request_id, &handle);
                    } else {
                        send_status(channel, session, request_id, 2, "Diretório não existe");
                    }
                } else {
                    send_status(channel, session, request_id, 3, "Acesso Negado");
                }
            }
            12 => { // SSH_FXP_READDIR
                let handle = reader.read_string().unwrap_or_default();
                if let Some(entries) = self.open_dirs.get_mut(&handle) {
                    if entries.is_empty() {
                        send_status(channel, session, request_id, 1, "EOF");
                    } else {
                        let mut w = SftpWriter::new();
                        w.write_u8(104); // NAME
                        w.write_u32(request_id);

                        let count = std::cmp::min(20, entries.len());
                        w.write_u32(count as u32);
                        for _ in 0..count {
                            let entry = entries.remove(0);
                            let name = entry.file_name().to_string_lossy().into_owned();
                            let is_dir = entry.file_type().map(|t| t.is_dir()).unwrap_or(false);
                            let longname = if is_dir { format!("drwxr-xr-x 1 user group 0 Jan 1 00:00 {}", name) }
                            else { format!("-rw-r--r-- 1 user group 0 Jan 1 00:00 {}", name) };
                            w.write_string(&name);
                            w.write_string(&longname);
                            w.write_u32(0);
                        }

                        let mut cv = CryptoVec::new();
                        cv.extend(&w.finish());
                        let _ = session.data(channel, cv);
                    }
                } else {
                    send_status(channel, session, request_id, 2, "Handle Inválido");
                }
            }
            13 => { // SSH_FXP_REMOVE
                let path = reader.read_string().unwrap_or_default();
                if let Some(safe_path) = self.build_safe_path(&path) {
                    match fs::remove_file(&safe_path) {
                        Ok(_) => send_status(channel, session, request_id, 0, "OK"),
                        Err(e) => send_status(channel, session, request_id, 4, &e.to_string()),
                    }
                } else {
                    send_status(channel, session, request_id, 3, "Acesso Negado");
                }
            }
            14 => { // SSH_FXP_MKDIR
                let path = reader.read_string().unwrap_or_default();
                if let Some(safe_path) = self.build_safe_path(&path) {
                    match fs::create_dir(&safe_path) {
                        Ok(_) => send_status(channel, session, request_id, 0, "OK"),
                        Err(e) => send_status(channel, session, request_id, 4, &e.to_string()),
                    }
                } else {
                    send_status(channel, session, request_id, 3, "Acesso Negado");
                }
            }
            15 => { // SSH_FXP_RMDIR
                let path = reader.read_string().unwrap_or_default();
                if let Some(safe_path) = self.build_safe_path(&path) {
                    match fs::remove_dir(&safe_path) {
                        Ok(_) => send_status(channel, session, request_id, 0, "OK"),
                        Err(e) => send_status(channel, session, request_id, 4, &e.to_string()),
                    }
                } else {
                    send_status(channel, session, request_id, 3, "Acesso Negado");
                }
            }
            16 => { // SSH_FXP_REALPATH
                let path = reader.read_string().unwrap_or_default();
                let mut fake_path = path;
                if fake_path.is_empty() || fake_path == "." { fake_path = "/".to_string(); }

                let mut w = SftpWriter::new();
                w.write_u8(104); // NAME
                w.write_u32(request_id);
                w.write_u32(1); // Retorna exatamente 1 caminho
                w.write_string(&fake_path);
                w.write_string(&format!("drwxr-xr-x 1 root root 0 Jan 1 00:00 {}", fake_path));
                w.write_u32(0);

                let mut cv = CryptoVec::new();
                cv.extend(&w.finish());
                let _ = session.data(channel, cv);
            }
            17 | 7 => { // SSH_FXP_STAT | SSH_FXP_LSTAT
                let path = reader.read_string().unwrap_or_default();
                if let Some(safe_path) = self.build_safe_path(&path) {
                    if let Ok(meta) = fs::symlink_metadata(&safe_path) {
                        let mut w = SftpWriter::new();
                        w.write_u8(105); // ATTRS
                        w.write_u32(request_id);
                        w.write_u32(0x00000001 | 0x00000004); // SIZE | PERMISSIONS
                        w.write_u64(meta.len());
                        let perms = if meta.is_dir() { 0x4000 | 0o755 } else { 0x8000 | 0o644 };
                        w.write_u32(perms);

                        let mut cv = CryptoVec::new();
                        cv.extend(&w.finish());
                        let _ = session.data(channel, cv);
                    } else {
                        send_status(channel, session, request_id, 2, "Arquivo não encontrado");
                    }
                } else {
                    send_status(channel, session, request_id, 3, "Acesso Negado");
                }
            }
            18 => { // SSH_FXP_RENAME
                let old_path = reader.read_string().unwrap_or_default();
                let new_path = reader.read_string().unwrap_or_default();
                if let (Some(safe_old), Some(safe_new)) = (self.build_safe_path(&old_path), self.build_safe_path(&new_path)) {
                    match fs::rename(safe_old, safe_new) {
                        Ok(_) => send_status(channel, session, request_id, 0, "OK"),
                        Err(e) => send_status(channel, session, request_id, 4, &e.to_string()),
                    }
                } else {
                    send_status(channel, session, request_id, 3, "Acesso Negado");
                }
            }
            _ => {
                send_status(channel, session, request_id, 4, "Comando não suportado");
            }
        }
        Ok(())
    }
}

#[async_trait]
impl Handler for SftpServer {
    type Error = anyhow::Error;

    async fn auth_password(&mut self, user: &str, pass: &str) -> Result<Auth, Self::Error> {
        if self.validate_with_csharp(user, pass) {
            Ok(Auth::Accept)
        } else {
            send_event(2, &format!("IP {} - Falha de login para {}", self.client_ip, user));
            Ok(Auth::Reject { proceed_with_methods: None })
        }
    }

    async fn channel_open_session(&mut self, _channel: Channel<Msg>, _session: &mut Session) -> Result<bool, Self::Error> {
        Ok(true)
    }

    // Corrigido e adaptado para a versão do russh atual
    async fn subsystem_request(&mut self, channel: ChannelId, name: &str, session: &mut Session) -> Result<(), Self::Error> {
        if name == "sftp" {
            send_event(101, &format!("IP {} - SFTP Iniciado!", self.client_ip));
            let _ = session.channel_success(channel);
        } else {
            let _ = session.channel_failure(channel);
        }
        Ok(())
    }

    // A Mágica de Captura de Dados: Onde roteamos as mensagens para o SFTP Engine
    async fn data(&mut self, channel: ChannelId, data: &[u8], session: &mut Session) -> Result<(), Self::Error> {
        self.sftp_buffer.extend_from_slice(data);

        while self.sftp_buffer.len() >= 4 {
            let len = u32::from_be_bytes(self.sftp_buffer[0..4].try_into().unwrap()) as usize;
            if self.sftp_buffer.len() < 4 + len {
                break;
            }

            let packet = self.sftp_buffer[4..4 + len].to_vec();
            self.sftp_buffer.drain(0..4 + len);

            if let Err(e) = self.handle_sftp_packet(channel, &packet, session) {
                send_event(4, &format!("Falha no processamento SFTP: {}", e));
            }
        }

        Ok(())
    }
}

// ==========================================
// 4. ENTRYPOINT (CHAMADO PELO C#)
// ==========================================

#[unsafe(no_mangle)] // Atualizado para nova regra do Rust em relação ao lint
pub extern "C" fn StartPlumeSFTP(
    port: c_int,
    key_path: *const c_char,
    auth_cb: Option<AuthCallback>,
    event_cb: Option<EventCallback>,
) -> c_int {
    unsafe {
        AUTH_CB = auth_cb;
        EVENT_CB = event_cb;
    }

    let key_path_str = if key_path.is_null() {
        String::from("sftp_host_key.pem")
    } else {
        unsafe { CStr::from_ptr(key_path).to_string_lossy().into_owned() }
    };

    send_event(10, &format!("Inicializando Rust Core na porta {}", port));

    let rt = match Runtime::new() {
        Ok(rt) => rt,
        Err(e) => {
            send_event(20, &format!("Falha ao inicializar motor Tokio: {}", e));
            return -1;
        }
    };

    rt.block_on(async move {
        if let Err(e) = run_ssh_server(port, &key_path_str).await {
            send_event(21, &format!("Erro catastrófico no SSH: {}", e));
        }
    });

    0
}

async fn run_ssh_server(port: c_int, _key_path: &str) -> anyhow::Result<()> {
    let mut config = russh::server::Config {
        inactivity_timeout: Some(std::time::Duration::from_secs(3600)),
        auth_rejection_time: std::time::Duration::from_secs(3),
        auth_rejection_time_initial: Some(std::time::Duration::from_secs(0)),
        ..Default::default()
    };

    let key = russh_keys::key::KeyPair::generate_ed25519().unwrap();
    config.keys.push(key);

    let config = Arc::new(config);
    let addr = format!("0.0.0.0:{}", port);
    let listener = TcpListener::bind(&addr).await?;

    send_event(11, &format!("Escutando em {}", addr));

    loop {
        let (stream, remote_addr) = listener.accept().await?;
        let ip = remote_addr.ip().to_string();
        send_event(12, &ip);

        let handler = SftpServer::new(ip);
        let config_clone = Arc::clone(&config);

        tokio::spawn(async move {
            match russh::server::run_stream(config_clone, stream, handler).await {
                Ok(_) => {},
                Err(e) => send_event(2, &format!("Erro na conexão do cliente: {}", e)),
            }
        });
    }
}