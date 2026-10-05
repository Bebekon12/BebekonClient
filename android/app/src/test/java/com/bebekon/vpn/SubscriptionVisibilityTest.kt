package com.bebekon.vpn

import org.junit.Assert.*
import org.junit.Test

class SubscriptionVisibilityTest {
    @Test fun hiddenSubscriptionKeepsSelectionAndRoutingNodes() {
        val node = Node("fixture", "Sweden", "{\"type\":\"vless\",\"server\":\"example.com\",\"server_port\":443}")
        val subscription = Subscription(name = "Fixture", source = "manual:fixture", nodes = listOf(node), hidden = true)
        val saved = SavedState(subscriptions = listOf(subscription), selected = node.id)
        assertTrue(saved.visibleNodes.isEmpty())
        assertEquals(node, saved.selectedNode)
        assertEquals(listOf(node), saved.nodes)
        assertTrue(Subscription.fromJson(subscription.toJson()).hidden)
        assertEquals(listOf(node), saved.copy(subscriptions = listOf(subscription.copy(hidden = false))).visibleNodes)
        val old = subscription.toJson().apply { remove("hidden") }
        assertFalse(Subscription.fromJson(old).hidden)
    }
}
