package main

/*
#include <stdlib.h>
#include <stdint.h>

typedef int (*AuthCallback)(const char* username, const char* password, const char* serverId, double* outQuota, char* outPath, int maxPathLen);
typedef void (*EventCallback)(int eventCode, const char* contextData);

static int safeCallAuth(void* cbPtr, const char* user, const char* pass, const char* serverId, double* quota, char* outPath, int maxPathLen) {
    if (!cbPtr) return 0;
    AuthCallback cb = (AuthCallback)cbPtr;
    return cb(user, pass, serverId, quota, outPath, maxPathLen);
}

static void safeCallEvent(void* cbPtr, int eventCode, const char* contextData) {
    if (!cbPtr) return;
    EventCallback cb = (EventCallback)cbPtr;
    cb(eventCode, contextData);
}
*/
import "C"

import (
    "crypto/rand"
    "crypto/rsa"
    "crypto/x509"
    "encoding/pem"
    "fmt"
    "io"
    "net"
    "os"
    "path/filepath"
    "strconv"
    "strings"
    "sync"
    "unsafe"

    "github.com/pkg/sftp"
    "golang.org/x/crypto/ssh"
)

var (
    globalPort     int
    globalKeyPath  string
    globalAuthCb   unsafe.Pointer
    globalEventCb  unsafe.Pointer
)

// Emissor de eventos por ID (Deixa o C# formatar os textos)
func sendEvent(code int, ctx string) {
    if globalEventCb == nil {
        return
    }
    cCtx := C.CString(ctx)
    defer C.free(unsafe.Pointer(cCtx))
    C.safeCallEvent(globalEventCb, C.int(code), cCtx)
}

// ==========================================
// 1. JAILED FS (Sistema de Arquivos Seguro)
// ==========================================
type jailedFS struct {
    root      string
    quota     int64
    usedBytes int64
    mu        sync.Mutex
}

func (fs *jailedFS) recalcSize() {
    fs.mu.Lock()
    defer fs.mu.Unlock()
    var size int64
    filepath.Walk(fs.root, func(_ string, info os.FileInfo, err error) error {
       if err == nil && !info.IsDir() { size += info.Size() }
       return nil
    })
    fs.usedBytes = size
}

func (fs *jailedFS) getPath(p string) (string, error) {
    p = filepath.ToSlash(p)
    p = strings.TrimPrefix(p, "/")
    cleanRoot := filepath.Clean(fs.root)
    cleanPath := filepath.Clean(filepath.Join(cleanRoot, p))
    if cleanPath != cleanRoot && !strings.HasPrefix(cleanPath, cleanRoot+string(filepath.Separator)) {
       return "", os.ErrPermission
    }
    return cleanPath, nil
}

func (fs *jailedFS) Fileread(req *sftp.Request) (io.ReaderAt, error) {
    path, err := fs.getPath(req.Filepath)
    if err != nil { return nil, err }
    return os.Open(path)
}

type quotaWriter struct {
    f   *os.File
    jfs *jailedFS
}

func (qw *quotaWriter) WriteAt(p []byte, off int64) (n int, err error) {
    qw.jfs.mu.Lock()
    defer qw.jfs.mu.Unlock()
    info, err := qw.f.Stat()
    if err != nil { return 0, err }
    currentSize := info.Size()
    endPos := off + int64(len(p))
    var diff int64
    if endPos > currentSize { diff = endPos - currentSize }
    if qw.jfs.quota > 0 && qw.jfs.usedBytes+diff > qw.jfs.quota { return 0, fmt.Errorf("cota excedida") }
    n, err = qw.f.WriteAt(p, off)
    if n > 0 && off+int64(n) > currentSize { qw.jfs.usedBytes += (off + int64(n)) - currentSize }
    return n, err
}

func (qw *quotaWriter) Close() error {
    err := qw.f.Close()
    qw.jfs.recalcSize()
    return err
}

func (fs *jailedFS) Filewrite(req *sftp.Request) (io.WriterAt, error) {
    path, err := fs.getPath(req.Filepath)
    if err != nil { return nil, err }
    pFlags := req.Pflags()
    var osFlags int
    if pFlags.Read && pFlags.Write { osFlags |= os.O_RDWR } else if pFlags.Write { osFlags |= os.O_WRONLY } else if pFlags.Read { osFlags |= os.O_RDONLY }
    if pFlags.Append { osFlags |= os.O_APPEND }
    if pFlags.Creat { osFlags |= os.O_CREATE }
    if pFlags.Excl { osFlags |= os.O_EXCL }
    if pFlags.Trunc {
       osFlags |= os.O_TRUNC
       if info, err := os.Stat(path); err == nil {
          fs.mu.Lock()
          fs.usedBytes -= info.Size()
          if fs.usedBytes < 0 { fs.usedBytes = 0 }
          fs.mu.Unlock()
       }
    }
    file, err := os.OpenFile(path, osFlags, 0644)
    if err != nil { return nil, err }
    return &quotaWriter{f: file, jfs: fs}, nil
}

func (fs *jailedFS) Filecmd(req *sftp.Request) error {
    path, err := fs.getPath(req.Filepath)
    if err != nil { return err }
    var target string
    if req.Target != "" {
       target, err = fs.getPath(req.Target)
       if err != nil { return err }
    }
    var cmdErr error
    switch req.Method {
    case "Setstat": cmdErr = nil
    case "Rename": cmdErr = os.Rename(path, target)
    case "Rmdir", "Remove": cmdErr = os.Remove(path)
    case "Mkdir": cmdErr = os.Mkdir(path, 0755)
    case "Symlink": cmdErr = os.ErrPermission
    default: cmdErr = fmt.Errorf("não suportado")
    }
    if cmdErr == nil && (req.Method == "Remove" || req.Method == "Rmdir" || req.Method == "Rename") { fs.recalcSize() }
    return cmdErr
}

type listerAt []os.FileInfo
func (l listerAt) ListAt(f []os.FileInfo, offset int64) (int, error) {
    if offset >= int64(len(l)) { return 0, io.EOF }
    n := copy(f, l[offset:])
    if n < len(f) { return n, io.EOF }
    return n, nil
}

func (fs *jailedFS) Filelist(req *sftp.Request) (sftp.ListerAt, error) {
    path, err := fs.getPath(req.Filepath)
    if err != nil { return nil, err }
    switch req.Method {
    case "List":
       files, err := os.ReadDir(path)
       if err != nil { return nil, err }
       var infos []os.FileInfo
       for _, f := range files { if info, err := f.Info(); err == nil { infos = append(infos, info) } }
       return listerAt(infos), nil
    case "Stat":
       info, err := os.Stat(path)
       if err != nil { return nil, err }
       return listerAt([]os.FileInfo{info}), nil
    case "Readlink": return nil, os.ErrPermission
    default: return nil, fmt.Errorf("não suportado")
    }
}

// ==========================================
// 2. ENTRYPOINT EXPOSTO PARA O C# (SÍNCRONO)
// ==========================================

//export StartPlumeSFTP
func StartPlumeSFTP(port C.int, cKeyPath *C.char, authCb unsafe.Pointer, eventCb unsafe.Pointer) C.int {
    globalPort = int(port)
    globalKeyPath = C.GoString(cKeyPath)
    globalAuthCb = authCb
    globalEventCb = eventCb

    // Prende a thread no laço de rede
    sftpServerLoop()
    
    return 0
}

// ==========================================
// 3. O LOOP NATIVO DO SFTP
// ==========================================

func sftpServerLoop() {
    portStr := fmt.Sprintf("%d", globalPort)
    sendEvent(10, portStr) // Evento 10: Iniciando

    var hostKey ssh.Signer
    keyBytes, err := os.ReadFile(globalKeyPath)
    if err != nil {
       priv, _ := rsa.GenerateKey(rand.Reader, 2048)
       privDer := x509.MarshalPKCS1PrivateKey(priv)
       privBlk := pem.Block{Type: "RSA PRIVATE KEY", Bytes: privDer}
       keyBytes = pem.EncodeToMemory(&privBlk)
       
       os.MkdirAll(filepath.Dir(globalKeyPath), 0755)
       os.WriteFile(globalKeyPath, keyBytes, 0600)
    }
    
    hostKey, err = ssh.ParsePrivateKey(keyBytes)
    if err != nil {
       sendEvent(20, fmt.Sprintf("SSH Key Error: %v", err)) // Evento 20: Erro de chave 
       return
    }

    sshConfig := &ssh.ServerConfig{
       PasswordCallback: func(c ssh.ConnMetadata, pass []byte) (*ssh.Permissions, error) {
          parts := strings.Split(c.User(), "_")
          if len(parts) < 2 {
             return nil, fmt.Errorf("invalido")
          }
          serverID := parts[len(parts)-1]
          userName := strings.Join(parts[:len(parts)-1], "_")

          var quota C.double
          outPathBuf := (*C.char)(C.malloc(1024))
          defer C.free(unsafe.Pointer(outPathBuf))
          
          cUser := C.CString(userName)
          cPass := C.CString(string(pass))
          cServerID := C.CString(serverID)
          defer C.free(unsafe.Pointer(cUser))
          defer C.free(unsafe.Pointer(cPass))
          defer C.free(unsafe.Pointer(cServerID))

          valid := C.safeCallAuth(globalAuthCb, cUser, cPass, cServerID, &quota, outPathBuf, 1024)

          if valid == 1 {
             fullRootPath := C.GoString(outPathBuf)
             return &ssh.Permissions{
                Extensions: map[string]string{
                   "root_path": fullRootPath,
                   "quota":     fmt.Sprintf("%f", float64(quota)),
                },
             }, nil
          }
          
          return nil, fmt.Errorf("negado")
       },
    }
    sshConfig.AddHostKey(hostKey)

    // O "::" em Go abre um Listener duplo: funciona para IPv4 e IPv6!
    listenAddr := fmt.Sprintf(":%d", globalPort)
    
    listener, err := net.Listen("tcp", listenAddr)
    if err != nil {
       sendEvent(22, portStr) // Evento 22: Falha no Bind
       return
    }

    sendEvent(11, portStr) // Evento 11: Porta aberta com sucesso!

    defer func() {
       if r := recover(); r != nil {
          sendEvent(23, fmt.Sprintf("Go Panic: %v", r)) // Panic Handler
       }
    }()

    for {
       nConn, err := listener.Accept()
       if err != nil { 
          continue 
       }
       
       remoteAddr := nConn.RemoteAddr().String()
       sendEvent(12, remoteAddr) // Evento 12: Nova conexão intercetada

       go func(conn net.Conn) {
          sshConn, chans, reqs, err := ssh.NewServerConn(conn, sshConfig)
          if err != nil { 
             return 
          }
          defer sshConn.Close()

          go ssh.DiscardRequests(reqs)

          for newChannel := range chans {
             if newChannel.ChannelType() != "session" {
                newChannel.Reject(ssh.UnknownChannelType, "unknown channel type")
                continue
             }
             channel, requests, err := newChannel.Accept()
             if err != nil { 
                 continue 
             }

             go func(in <-chan *ssh.Request) {
                for req := range in {
                   if req.Type == "subsystem" && string(req.Payload[4:]) == "sftp" {
                      req.Reply(true, nil)

                      rootPath := sshConn.Permissions.Extensions["root_path"]
                      quotaStr := sshConn.Permissions.Extensions["quota"]
                      quotaFloat, _ := strconv.ParseFloat(quotaStr, 64)

                      os.MkdirAll(rootPath, 0755)

                      jfs := &jailedFS{root: rootPath, quota: int64(quotaFloat * 1024 * 1024)}
                      jfs.recalcSize()

                      handlers := sftp.Handlers{FileGet: jfs, FilePut: jfs, FileCmd: jfs, FileList: jfs}
                      server := sftp.NewRequestServer(channel, handlers)
                      
                      server.Serve()
                   }
                }
             }(requests)
          }
       }(nConn)
    }
}

func main() {}