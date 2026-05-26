package Plume.Docker

import com.github.dockerjava.api.DockerClient
import com.github.dockerjava.core.DefaultDockerClientConfig
import com.github.dockerjava.core.DockerClientImpl
import com.github.dockerjava.httpclient5.ApacheDockerHttpClient
import java.time.Duration

object DockerClientFactory {
    fun fromEnv(): DockerClient {
        try {
            val cfg = DefaultDockerClientConfig.createDefaultConfigBuilder().build()
            val http = ApacheDockerHttpClient.Builder()
                .dockerHost(cfg.dockerHost)
                .sslConfig(cfg.sslConfig)
                .maxConnections(100)
                .connectionTimeout(Duration.ofSeconds(10))
                .responseTimeout(Duration.ofSeconds(60))
                .build()
            return DockerClientImpl.getInstance(cfg, http)
        } catch (e: Exception) {
            throw e
        }
    }
}