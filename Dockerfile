# Usa o Debian Slim que é super leve, mas tem a glibc compatível com seus binários NativeAOT
FROM debian:bookworm-slim

# Instala certificados CA (útil caso o app faça requisições HTTPS) e limpa o cache para manter a imagem pequena
RUN apt-get update && apt-get install -y ca-certificates && rm -rf /var/lib/apt/lists/*

# TARGETARCH é uma variável mágica do Docker Buildx.
# Quando a Action buildar para linux/amd64, ela valerá "amd64".
# Quando buildar para linux/arm64, ela valerá "arm64".
ARG TARGETARCH

WORKDIR /app

# Copia o binário EXATO da pasta artifacts gerada pela GitHub Action
# Isso vai copiar artifacts/plume-linux-amd64 ou artifacts/plume-linux-arm64 dependendo da arquitetura
COPY artifacts/plume-linux-${TARGETARCH} /app/plume

# Dá permissão de execução
RUN chmod +x /app/plume

# Cria o diretório para o arquivo de configuração do Plume
RUN mkdir -p /etc/plume

# Define as portas padrões (Normal/API e SFTP)
ENV PORT=8080
ENV SFTP_PORT=2022

# Expõe as portas
EXPOSE $PORT
EXPOSE $SFTP_PORT

# Roda o executável
CMD ["/app/plume"]