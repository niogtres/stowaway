# Access over the internet

To reach Stowaway from anywhere, put it behind a reverse proxy you already run, such as nginx behind Cloudflare. Your public certificate stays on the proxy, and Stowaway only accepts connections from the proxy.

```
browser ──https──▶ Cloudflare ──https (origin pull)──▶ nginx ──https──▶ Stowaway (this computer, port 8484)
```

**1. In Stowaway** (Settings → Remote access, on this computer):

- **Names:** the public name, for example `backup.example.com`.
- **Reverse proxy:** the LAN address of the nginx server. From then on, only nginx can connect (Windows Firewall and Stowaway both enforce it). The visitor's address that nginx forwards is used for lockouts, rate limits, alerts and the audit log.
- **Certificate:** *Issued by Stowaway* is fine here, because only nginx sees it.
- **Save** and **Restart service**.

**2. In nginx**, a server block like this (adjust names, paths and addresses):

```nginx
server {
    listen 443 ssl;
    server_name backup.example.com;

    ssl_certificate     /etc/nginx/certs/example.com/fullchain.pem;
    ssl_certificate_key /etc/nginx/certs/example.com/privkey.pem;

    # Only Cloudflare may connect (Authenticated Origin Pulls).
    ssl_client_certificate /etc/nginx/certs/cloudflare-origin-pull-ca.pem;
    ssl_verify_client on;

    client_max_body_size 1m;

    location / {
        proxy_pass https://192.168.1.20:8484;          # this computer
        proxy_http_version 1.1;

        proxy_set_header Host              $host;
        # The visitor, as Cloudflare saw them. Safe because only Cloudflare gets past ssl_verify_client.
        proxy_set_header X-Forwarded-For   $http_cf_connecting_ip;
        proxy_set_header X-Forwarded-Proto https;

        # Live updates (WebSocket).
        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600s;
    }
}
```

**3. In Cloudflare:**

- Add `backup` as a **proxied** (orange cloud) DNS record pointing at your nginx, like your other sites. Use SSL mode **Full (strict)** and keep **Authenticated Origin Pulls** on.
- **Strongly recommended:** put a **Cloudflare Access** application in front (Zero Trust → Access → Applications → Self-hosted, for `backup.example.com`), with a policy that only allows your own email address. Cloudflare then asks for a one-time code sent to your email before anything reaches nginx. That's a second, independent lock in front of Stowaway's own sign-in, and it's free for small teams.

*Optional, stricter:* nginx encrypts the hop to Stowaway but doesn't check who answers. To make it check, download Stowaway's certificate at `https://localhost:8484/api/remote/certificate?format=pem`, copy it to the nginx server and add `proxy_ssl_trusted_certificate <that file>; proxy_ssl_verify on; proxy_ssl_name backup.example.com;`. You then have to copy the certificate again whenever you change the names in Stowaway.

With remote access through a proxy, computers on your LAN also go through the proxy (open the public name). On this computer, `https://localhost:8484` keeps working with Windows sign-in.

**See also:** [Remote access](12-remote-access.md), [Troubleshooting](14-troubleshooting.md)
