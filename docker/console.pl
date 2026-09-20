#!/usr/bin/perl
# Server console for a running container, over Source RCON (TCP on the game port).
#
#   docker compose exec deadworks console status        run one command, print the reply
#   docker compose exec deadworks console               interactive; ctrl-d to leave
#   console --ping                                      A2S_INFO probe, used by the HEALTHCHECK
#
# RCON rather than -netconport because the dedicated server does not open a netconsole, and its
# VConsole2 listener (port 29000) is a binary protocol meant for Valve's GUI tool. The password is
# whatever entrypoint.sh put in /data/rcon_password. Perl because debian-slim already ships it.
use strict;
use warnings;
use IO::Socket::INET;
use IO::Select;

my $port = $ENV{SERVER_PORT} || 27015;

# The engine binds the container's bridge address, not loopback. Take it from the listener itself
# rather than guessing which interface that is.
sub listen_addr {
    open my $fh, '<', '/proc/net/tcp' or return;
    while (<$fh>) {
        my (undef, $local, undef, $state) = split ' ';
        next unless defined $state && $state eq '0A' && $local =~ /^([0-9A-F]{8}):([0-9A-F]{4})$/;
        return join '.', reverse map { hex } $1 =~ /../g if hex $2 == $port;
    }
    return;
}

if (@ARGV && $ARGV[0] eq '--ping') {
    # Not having reached the server yet (first start is a ~35 GB download) is not unhealthy.
    exit 0 if -e '/tmp/deadworks-starting';
    my $addr = listen_addr() or exit 1;
    my $udp = IO::Socket::INET->new(PeerAddr => $addr, PeerPort => $port, Proto => 'udp') or exit 1;
    my $query = "\xff\xff\xff\xffTSource Engine Query\0";
    my $sel = IO::Select->new($udp);
    my $challenge = '';
    for (1 .. 2) {
        $udp->send($query . $challenge);
        $sel->can_read(3) or exit 1;
        $udp->recv(my $reply, 4096);
        exit 0 if substr($reply, 4, 1) eq 'I';
        $challenge = substr $reply, 5, 4;    # 'A': the server wants its challenge echoed back
    }
    exit 1;
}

my $addr = listen_addr() or die "console: the server is not up yet\n";
open my $pw_fh, '<', '/data/rcon_password' or die "console: no /data/rcon_password\n";
chomp(my $password = <$pw_fh> // '');

my $sock = IO::Socket::INET->new(PeerAddr => $addr, PeerPort => $port, Proto => 'tcp', Timeout => 5)
    or die "console: cannot reach $addr:$port: $!\n";
binmode $sock;
$| = 1;

my $sel = IO::Select->new($sock);
my $buf = '';

# A packet is: int32 size, int32 id, int32 type, body, two NULs. Little-endian.
sub send_packet {
    my ($id, $type, $body) = @_;
    my $packet = pack('V V', $id, $type) . $body . "\0\0";
    syswrite $sock, pack('V', length $packet) . $packet;
}

sub read_packet {
    while (1) {
        if (length $buf >= 4) {
            my $size = unpack 'V', $buf;
            if (length $buf >= 4 + $size) {
                my $packet = substr $buf, 0, 4 + $size, '';
                my ($id, $type) = unpack 'x4 l< l<', $packet;
                return ($id, $type, substr $packet, 12, $size - 10);
            }
        }
        $sel->can_read(10) or return;
        sysread($sock, $buf, 65536, length $buf) or return;
    }
}

send_packet(1, 3, $password);
while (1) {
    my ($id, $type) = read_packet() or die "console: no answer to login\n";
    next unless $type == 2;
    die "console: RCON password rejected\n" if $id == -1;
    last;
}

# A long reply is split across packets with nothing marking the last one, so every command is
# followed by an empty one: replies come back in order, and the empty command's reply ends ours.
my $next_id = 10;
sub run {
    my $cmd = shift;
    my $end = $next_id += 2;
    send_packet($end - 1, 2, $cmd);
    send_packet($end, 2, '');
    while (my ($id, undef, $body) = read_packet()) {
        return 1 if $id == $end;
        print $body;
    }
    return 0;
}

if (@ARGV) {
    exit(run("@ARGV") ? 0 : 1);
}

print "connected to $addr:$port; ctrl-d to leave\n";
while (defined(my $line = <STDIN>)) {
    chomp $line;
    next unless length $line;
    run($line) or die "console: connection lost\n";
    print "\n";
}
