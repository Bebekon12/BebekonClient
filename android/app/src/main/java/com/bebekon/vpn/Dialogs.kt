@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.bebekon.vpn

import android.content.Intent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog

@Composable fun SubscriptionDialog(initial: String, dismiss: () -> Unit, save: (String, String) -> Unit) {
    var source by remember(initial) { mutableStateOf(initial) }; var name by remember { mutableStateOf("") }
    AlertDialog(onDismissRequest = dismiss, icon = { Icon(Icons.Outlined.Link, null) }, title = { Text("Добавить подписку") }, text = { Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Ссылка провайдера или VPN конфигурация", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 13.sp)
        OutlinedTextField(source, { source = it }, Modifier.fillMaxWidth().heightIn(min = 100.dp, max = 180.dp), label = { Text("Ссылка / текст") }, shape = RoundedCornerShape(14.dp), maxLines = 5)
        OutlinedTextField(name, { name = it }, Modifier.fillMaxWidth(), label = { Text("Название — необязательно") }, placeholder = { Text("Определится автоматически") }, shape = RoundedCornerShape(14.dp), singleLine = true)
    } }, confirmButton = { Button({ save(source, name) }, enabled = source.isNotBlank()) { Text("Добавить") } }, dismissButton = { TextButton(dismiss) { Text("Отмена") } })
}
@Composable fun RuleDialog(existing: Rule?, dismiss: () -> Unit, save: (Rule) -> Unit) {
    var kind by remember { mutableStateOf(existing?.kind ?: RuleKind.DOMAIN) }; var value by remember { mutableStateOf(existing?.values?.joinToString("\n") ?: "") }; var name by remember { mutableStateOf(existing?.name ?: "") }; var vpn by remember { mutableStateOf(existing?.vpn ?: true) }; var menu by remember { mutableStateOf(false) }; var appPicker by remember { mutableStateOf(false) }; var error by remember { mutableStateOf("") }
    val context = LocalContext.current
    AlertDialog(onDismissRequest = dismiss, icon = { Icon(Icons.Outlined.Route, null) }, title = { Text(if (existing == null) "Новое правило" else "Изменить правило") }, text = {
        Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Box { OutlinedButton({ menu = true }, Modifier.fillMaxWidth(), shape = RoundedCornerShape(14.dp)) { Text(kind.label, Modifier.weight(1f)); Icon(Icons.Outlined.ExpandMore, null) }; DropdownMenu(menu, { menu = false }) { RuleKind.entries.forEach { k -> DropdownMenuItem(text = { Text(k.label) }, onClick = { kind = k; menu = false }) } } }
            OutlinedTextField(value, { value = it }, Modifier.fillMaxWidth(), label = { Text("Значение") }, placeholder = { Text(when (kind) { RuleKind.DOMAIN -> "example.com"; RuleKind.KEYWORD -> "openai"; RuleKind.APP -> "org.telegram.messenger"; RuleKind.CIDR -> "192.168.0.0/16"; RuleKind.GEOSITE -> "youtube"; RuleKind.GEOIP -> "telegram" }) }, shape = RoundedCornerShape(14.dp), maxLines = 4)
            if (kind == RuleKind.APP) TextButton({ appPicker = true }) { Icon(Icons.Outlined.Apps, null, Modifier.size(18.dp)); Text("Выбрать приложение", Modifier.padding(start = 6.dp)) }
            if (kind in listOf(RuleKind.GEOSITE, RuleKind.GEOIP)) Text(if (kind == RuleKind.GEOSITE) "youtube, telegram, openai, anthropic, discord, netflix, tiktok, google, twitch, whatsapp, instagram, facebook, twitter" else "ru, de, nl, se, fi, ee, lv, lt, pl, fr, gb, us, ua, kz, cn, telegram", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            OutlinedTextField(name, { name = it }, Modifier.fillMaxWidth(), label = { Text("Название — необязательно") }, shape = RoundedCornerShape(14.dp), singleLine = true)
            Row(verticalAlignment = Alignment.CenterVertically) { Text(if (vpn) "Через VPN" else "Напрямую", Modifier.weight(1f), fontWeight = FontWeight.SemiBold); Switch(vpn, { vpn = it }) }
            Text("Новые правила имеют приоритет. Изменения применяются к текущему соединению.", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (error.isNotEmpty()) Text(error, color = MaterialTheme.colorScheme.error, fontSize = 12.sp)
        }
    }, confirmButton = { Button({
        val values = value.split('\n', ',').map(String::trim).filter(String::isNotEmpty)
        val r = Rule(id = existing?.id ?: java.util.UUID.randomUUID().toString(), name = name.ifBlank { values.firstOrNull() ?: "Правило" }, kind = kind, values = values, vpn = vpn, created = existing?.created ?: System.currentTimeMillis())
        try { CoreConfig.validateRule(r); if (kind in listOf(RuleKind.GEOSITE, RuleKind.GEOIP)) values.forEach { context.repo.geo((if (kind == RuleKind.GEOSITE) "geosite-" else "geoip-") + it) }; save(r) } catch (e: Exception) { error = e.message ?: "Проверьте значение" }
    }) { Text("Сохранить") } }, dismissButton = { TextButton(dismiss) { Text("Отмена") } })
    if (appPicker) AppPicker({ appPicker = false }) { packageName, label -> value = packageName; if (name.isEmpty()) name = label; appPicker = false }
}
@Composable private fun AppPicker(dismiss: () -> Unit, choose: (String, String) -> Unit) {
    val context = LocalContext.current; var search by remember { mutableStateOf("") }
    val apps = remember { context.packageManager.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0).map { it.activityInfo.packageName to it.loadLabel(context.packageManager).toString() }.distinctBy { it.first }.filter { it.first != context.packageName }.sortedBy { it.second.lowercase() } }
    AlertDialog(onDismissRequest = dismiss, title = { Text("Приложения") }, text = { Column { OutlinedTextField(search, { search = it }, label = { Text("Поиск") }, singleLine = true); LazyColumn(Modifier.heightIn(max = 360.dp)) { items(apps.filter { it.first.contains(search, true) || it.second.contains(search, true) }, key = { it.first }) { (pkg, label) -> Column(Modifier.fillMaxWidth().clickable { choose(pkg, label) }.padding(vertical = 13.dp)) { Text(label, fontWeight = FontWeight.SemiBold); Text(pkg, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) } } } } }, confirmButton = { TextButton(dismiss) { Text("Отмена") } })
}
@Composable fun PresetsDialog(model: MainViewModel, dismiss: () -> Unit) {
    val presets = remember { model.repo.presets() }
    AlertDialog(onDismissRequest = dismiss, icon = { Icon(Icons.Outlined.AutoAwesome, null) }, title = { Text("Готовые наборы") }, text = { LazyColumn(Modifier.heightIn(max = 430.dp)) {
        item { Text("Наборы дополняют ваши правила. Windows программы пропущены: на Android выбирайте приложения отдельно.", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 11.sp, modifier = Modifier.padding(bottom = 12.dp)) }
        items(presets) { (name, rules) -> Row(Modifier.fillMaxWidth().clickable { model.preset(rules); dismiss() }.padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) { Column(Modifier.weight(1f)) { Text(name, fontWeight = FontWeight.SemiBold); Text("${rules.size} правил", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant) }; Icon(Icons.Outlined.AddCircleOutline, "Добавить набор", tint = MaterialTheme.colorScheme.primary) } }
    } }, confirmButton = { TextButton(dismiss) { Text("Закрыть") } })
}
