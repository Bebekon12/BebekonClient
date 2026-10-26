package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test

class SleepRecoveryTest {
    @Test fun shortGlancesChangeNothing() {
        assertFalse(SleepRecovery.slept(10_000))
        assertFalse(SleepRecovery.shouldReset(10_000, 600_000))
    }
    @Test fun idleTunnelIsResetAfterSleep() = assertTrue(SleepRecovery.shouldReset(5 * 60_000, 5 * 60_000))
    @Test fun streamingTunnelIsOnlyChecked() {
        assertTrue(SleepRecovery.slept(5 * 60_000))
        assertFalse(SleepRecovery.shouldReset(5 * 60_000, 3_000))
    }
}
