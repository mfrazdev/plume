package Plume

import org.slf4j.LoggerFactory

object Logger {

    lateinit var logger : org.slf4j.Logger
        internal set


    fun init(classe: Class<*>) {
        logger = LoggerFactory.getLogger(classe)
    }

}