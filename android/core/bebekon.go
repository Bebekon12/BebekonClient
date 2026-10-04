// SPDX-License-Identifier: GPL-3.0-or-later
// Bebekon additions to the official, pinned libbox. Kept separate for reproducible builds.
package libbox

import (
 "context"
 "crypto/tls"
 "io"
 "net"
 "net/http"
 "net/url"
 "os"
 "strconv"
 "syscall"
 "time"
 box "github.com/sagernet/sing-box"
 "github.com/sagernet/sing-box/adapter"
 "github.com/sagernet/sing-box/common/certificate"
 "github.com/sagernet/sing-box/option"
 M "github.com/sagernet/sing/common/metadata"
 "github.com/sagernet/sing/common/logger"
 "github.com/sagernet/sing/service"
)

type BebekonProbeResult struct { Millis int32; Body string; StatusCode int32 }

// A separate outbound instance probes the selected VPN, without changing the active VPN or routing.
// One context bounds DNS, proxy handshake, TLS and response body to five seconds.
func BebekonProbe(config string, platform PlatformInterface, tag, target, method string) (*BebekonProbeResult, error) {
 ctx, cancel := context.WithTimeout(baseContext(platform), 5*time.Second)
 defer cancel()
 wrapper := &platformInterfaceWrapper{iif: platform, useProcFS: false}
 ctx = service.ContextWith[adapter.PlatformInterface](ctx, wrapper)
 options, err := parseConfig(ctx, config); if err != nil { return nil, err }
 instance, err := box.New(box.Options{Context: ctx, Options: options}); if err != nil { return nil, err }
 defer instance.Close()
 if err = instance.Start(); err != nil { return nil, err }
 outbound, found := instance.Outbound().Outbound(tag); if !found { return nil, os.ErrInvalid }
 u, err := url.Parse(target); if err != nil || u.Scheme != "https" || (method != "GET" && method != "HEAD") { return nil, os.ErrInvalid }
 store, err := certificate.NewStore(ctx, logger.NOP(), option.CertificateOptions{}); if err != nil { return nil, err }
 defer store.Close()
 transport := &http.Transport{DisableKeepAlives:true, TLSClientConfig:&tls.Config{RootCAs:store.Pool(), MinVersion:tls.VersionTLS12}, DialContext:func(c context.Context, network, address string)(net.Conn,error){ return outbound.DialContext(c, network, M.ParseSocksaddr(address)) }}
 defer transport.CloseIdleConnections()
 client := &http.Client{Transport:transport, CheckRedirect:func(*http.Request,[]*http.Request)error{return http.ErrUseLastResponse}}
 request, err := http.NewRequestWithContext(ctx, method, target, nil); if err != nil { return nil, err }
 request.Header.Set("User-Agent", "BebekonAndroid/0.1.0")
 started := time.Now()
 response, err := client.Do(request); if err != nil { return nil, err }
 defer response.Body.Close()
 body, err := io.ReadAll(io.LimitReader(response.Body, 65537)); if err != nil || len(body)>65536 { return nil, os.ErrInvalid }
 if response.StatusCode < 200 || response.StatusCode >= 400 { return nil, &probeHTTPError{response.StatusCode} }
 return &BebekonProbeResult{Millis:int32(time.Since(started).Milliseconds()), Body:string(body), StatusCode:int32(response.StatusCode)}, nil
}
type probeHTTPError struct { code int }; func(e *probeHTTPError)Error()string{return "probe HTTP "+strconv.Itoa(e.code)}

// TCP timeout also includes name resolution; no UI executor is left waiting on DNS.
func BebekonTCPProbe(host string, port int32, platform PlatformInterface) (int32, error) {
 if port < 1 || port > 65535 { return 0, os.ErrInvalid }
 ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
 defer cancel()
 dialer := net.Dialer{Control:func(network, address string, raw syscall.RawConn)error{
   var protectErr error
   err := raw.Control(func(fd uintptr){ protectErr = platform.AutoDetectInterfaceControl(int32(fd)) })
   if err != nil { return err }; return protectErr
 }}
 started := time.Now()
 connection, err := dialer.DialContext(ctx, "tcp", net.JoinHostPort(host, strconv.Itoa(int(port))))
 if err != nil { return 0, err }; connection.Close()
 return int32(time.Since(started).Milliseconds()), nil
}
