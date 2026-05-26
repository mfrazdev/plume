package Plume

import java.io.ByteArrayOutputStream
import java.io.OutputStream
import java.io.PrintStream

object CrashHandler {

    private const val RED = "\u001B[31m"
    private const val YELLOW = "\u001B[33m"
    private const val CYAN = "\u001B[36m"
    private const val BOLD = "\u001B[1m"
    private const val RESET = "\u001B[0m"

    fun install() {
        val defaultErr = System.err

        System.setErr(PrintStream(PlumeErrorParserStream(defaultErr), true, "UTF-8"))

        Thread.setDefaultUncaughtExceptionHandler { _, throwable ->
            throwable.printStackTrace()
        }
    }

    private class PlumeErrorParserStream(private val target: PrintStream) : OutputStream() {
        private val buffer = ByteArrayOutputStream()

        private val threadHeaderRegex = Regex("""^Exception in thread "([^"]+)" ([\w.$]+)(?:: (.*))?$""")
        private val causedByRegex = Regex("""^Caused by:\s+([\w.$]+)(?:: (.*))?$""")
        private val standardHeaderRegex = Regex("""^([a-zA-Z0-9_.$]+(?:Exception|Error|Throwable))(?:: (.*))?$""")

        private val traceLineRegex = Regex("""^\s*at\s+(.+?)\(([^)]+)\)$""")

        private var traceCount = 0
        private var inException = false

        @Synchronized
        override fun write(b: Int) {
            if (b == '\n'.code) {
                flushLine()
            } else if (b != '\r'.code) {
                buffer.write(b)
            }
        }

        @Synchronized
        override fun write(b: ByteArray, off: Int, len: Int) {
            for (i in off until off + len) {
                write(b[i].toInt())
            }
        }

        @Synchronized
        override fun flush() {
            if (buffer.size() > 0) flushLine()
            target.flush()
        }

        private fun flushLine() {
            val line = buffer.toString("UTF-8")
            buffer.reset()
            processLine(line)
        }

        private fun processLine(line: String) {
            val threadHeaderMatch = threadHeaderRegex.matchEntire(line)
            if (threadHeaderMatch != null) {
                printHeader(threadHeaderMatch.groupValues[1], threadHeaderMatch.groupValues[2], threadHeaderMatch.groupValues[3])
                return
            }

            val causedByMatch = causedByRegex.matchEntire(line)
            if (causedByMatch != null) {
                printHeader(Thread.currentThread().name, causedByMatch.groupValues[1], causedByMatch.groupValues[2], "CAUSED BY")
                return
            }

            val standardHeaderMatch = standardHeaderRegex.matchEntire(line)
            if (standardHeaderMatch != null) {
                printHeader(Thread.currentThread().name, standardHeaderMatch.groupValues[1], standardHeaderMatch.groupValues[2])
                return
            }

            val traceLineMatch = traceLineRegex.matchEntire(line)
            if (traceLineMatch != null) {
                var classAndMethod = traceLineMatch.groupValues[1]

                if (classAndMethod.contains("/")) {
                    classAndMethod = classAndMethod.substringAfterLast("/")
                }

                val className = classAndMethod.substringBeforeLast(".")
                val location = traceLineMatch.groupValues[2]

                // Filtro ajustado: removido "kotlin." e "kotlinx." para exibir stack traces completos do docker-java
                if (className.startsWith("java.") ||
                    className.startsWith("jdk.") ||
                    className.startsWith("com.sun.") ||
                    className.startsWith("android.") ||
                    className.startsWith("androidx.")) {
                    return
                }

                if (traceCount < 15) {
                    val parts = location.split(":")
                    val rawFileName = parts.getOrNull(0) ?: "Unknown"
                    val lineNumber = parts.getOrNull(1) ?: "Unknown"
                    val fileName = rawFileName.replace(".java", "").replace(".kt", "")

                    target.println("  $CYAN$fileName$RESET : $RED$lineNumber$RESET")
                    traceCount++
                }
                return
            }

            if (line.trim().startsWith("...") && inException) return

            inException = false
            target.println(line)
        }

        private fun printHeader(threadName: String, fullClassName: String, message: String?, prefix: String = "EXCEPTION") {
            traceCount = 0
            inException = true
            val simpleName = fullClassName.substringAfterLast('.')
            val msg = if (message.isNullOrBlank()) "" else ": $message"

            target.println()
            target.println("$RED$BOLD[ $prefix ]$RESET $YELLOW$threadName$RESET | $RED$simpleName$RESET$msg")
        }
    }
}