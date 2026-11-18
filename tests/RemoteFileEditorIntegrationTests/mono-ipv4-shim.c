// A test aid for machines whose kernel has no IPv6 (see RemoteFileEditorIntegrationTests.csproj): lets Mono create dual-mode "IPv6" sockets on a kernel without IPv6 by turning
// them into IPv4 sockets and converting IPv4-mapped addresses at connect/bind and back at getsockname/getpeername/accept.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <errno.h>
#include <netinet/in.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/types.h>

static int v6[4096];

static void to4(const struct sockaddr_in6 *a6, struct sockaddr_in *a4) {
  memset(a4, 0, sizeof *a4);
  a4->sin_family = AF_INET; a4->sin_port = a6->sin6_port;
  if (IN6_IS_ADDR_V4MAPPED(&a6->sin6_addr)) memcpy(&a4->sin_addr, &a6->sin6_addr.s6_addr[12], 4);
  else if (IN6_IS_ADDR_LOOPBACK(&a6->sin6_addr)) a4->sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  else a4->sin_addr.s_addr = htonl(INADDR_ANY);
}
static void to6(const struct sockaddr_in *a4, struct sockaddr_in6 *a6) {
  memset(a6, 0, sizeof *a6);
  a6->sin6_family = AF_INET6; a6->sin6_port = a4->sin_port;
  a6->sin6_addr.s6_addr[10] = 0xff; a6->sin6_addr.s6_addr[11] = 0xff; memcpy(&a6->sin6_addr.s6_addr[12], &a4->sin_addr, 4);
}

int socket(int domain, int type, int protocol) {
  int (*real)(int,int,int) = dlsym(RTLD_NEXT, "socket");
  if (domain == AF_INET6) { int fd = real(AF_INET, type, protocol == IPPROTO_IPV6 ? 0 : protocol); if (fd >= 0 && fd < 4096) v6[fd] = 1; return fd; }
  return real(domain, type, protocol);
}
int setsockopt(int fd, int level, int name, const void *val, socklen_t len) {
  int (*real)(int,int,int,const void*,socklen_t) = dlsym(RTLD_NEXT, "setsockopt");
  if (level == IPPROTO_IPV6) return 0;
  return real(fd, level, name, val, len);
}
int getsockopt(int fd, int level, int name, void *val, socklen_t *len) {
  int (*real)(int,int,int,void*,socklen_t*) = dlsym(RTLD_NEXT, "getsockopt");
  if (level == IPPROTO_IPV6) { if (val && len && *len >= sizeof(int)) { *(int*)val = 0; *len = sizeof(int); } return 0; }
  return real(fd, level, name, val, len);
}
int connect(int fd, const struct sockaddr *addr, socklen_t len) {
  int (*real)(int,const struct sockaddr*,socklen_t) = dlsym(RTLD_NEXT, "connect");
  if (addr && addr->sa_family == AF_INET6 && fd >= 0 && fd < 4096 && v6[fd]) { struct sockaddr_in a4; to4((const struct sockaddr_in6*)addr, &a4); return real(fd, (struct sockaddr*)&a4, sizeof a4); }
  return real(fd, addr, len);
}
int bind(int fd, const struct sockaddr *addr, socklen_t len) {
  int (*real)(int,const struct sockaddr*,socklen_t) = dlsym(RTLD_NEXT, "bind");
  if (addr && addr->sa_family == AF_INET6 && fd >= 0 && fd < 4096 && v6[fd]) { struct sockaddr_in a4; to4((const struct sockaddr_in6*)addr, &a4); return real(fd, (struct sockaddr*)&a4, sizeof a4); }
  return real(fd, addr, len);
}
static int conv_out(int fd, struct sockaddr *addr, socklen_t *len) {
  if (fd >= 0 && fd < 4096 && v6[fd] && addr && len && addr->sa_family == AF_INET) {
    struct sockaddr_in a4; memcpy(&a4, addr, sizeof a4); struct sockaddr_in6 a6; to6(&a4, &a6);
    socklen_t n = *len < sizeof a6 ? *len : sizeof a6; memcpy(addr, &a6, n); *len = sizeof a6;
  }
  return 0;
}
int getsockname(int fd, struct sockaddr *addr, socklen_t *len) { int (*real)(int,struct sockaddr*,socklen_t*) = dlsym(RTLD_NEXT, "getsockname"); int r = real(fd, addr, len); if (r == 0) conv_out(fd, addr, len); return r; }
int getpeername(int fd, struct sockaddr *addr, socklen_t *len) { int (*real)(int,struct sockaddr*,socklen_t*) = dlsym(RTLD_NEXT, "getpeername"); int r = real(fd, addr, len); if (r == 0) conv_out(fd, addr, len); return r; }
int accept(int fd, struct sockaddr *addr, socklen_t *len) { int (*real)(int,struct sockaddr*,socklen_t*) = dlsym(RTLD_NEXT, "accept"); int r = real(fd, addr, len); if (r >= 0) { if (r < 4096 && fd < 4096) v6[r] = v6[fd]; if (addr && len) { socklen_t l=*len; if (v6[fd]) conv_out(fd, addr, len); (void)l; } } return r; }
