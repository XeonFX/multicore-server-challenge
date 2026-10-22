// One event loop: its own listener (SO_REUSEPORT spreads connections over the loops), its own epoll, its own
// Postgres connections and its own bcrypt reply pipe. A client connection runs one request at a time; requests that
// need the database are Bind/Execute messages appended to a Postgres connection's output, and every request
// appended in one turn of the loop shares one Sync, so a turn's writes commit together.

import bcrypt;
import buf;
import crypto;
import http;
import json;
import jwt;
import pg;
import sql;
import std;
import sys;

// ---------- configuration, set by main before the loops start ----------

var listen_addr: u32 = 0;
var listen_port = 80;
var pg_addr: u32 = 0;
var pg_port = 5432;
var pg_user: u8[..128] = "";
var pg_password: u8[..256] = "";
var pg_db: u8[..128] = "";
var jwt_key = hmac_key("");
var readers = 3;

// ---------- state ----------

let KIND_CLIENT: u64 = 0;
let KIND_LISTENER: u64 = 1;
let KIND_PG: u64 = 2;
let KIND_PIPE: u64 = 3;

// What a busy connection waits for.
let K_REGISTER_HASH = 1;
let K_REGISTER = 2;
let K_LOGIN = 3;
let K_LOGIN_CHECK = 4;
let K_CURRENT = 5;
let K_UPDATE_HASH = 6;
let K_UPDATE_USER = 7;
let K_PROFILE = 8;
let K_FOLLOW = 9;
let K_UNFOLLOW = 10;
let K_LIST = 11;
let K_ARTICLE = 12;
let K_CREATE = 13;
let K_UPDATE_ARTICLE = 14;
let K_DELETE_ARTICLE = 15;
let K_FAVORITE = 16;
let K_UNFAVORITE = 17;
let K_COMMENTS = 18;
let K_CREATE_COMMENT = 19;
let K_DELETE_COMMENT = 20;
let K_TAGS = 21;

struct Conn {
    fd: i32 = -1,
    gen: u32 = 0,
    busy: bool = false,         // a request is being answered
    dead: bool = false,         // closed while busy: the slot is freed when the answer comes
    close_after: bool = false,
    write: bool = false,        // the request runs on the writer connection
    group_end: bool = false,    // the last request of its Sync group
    kind: i64 = 0,
    viewer: i64 = 0,
    nstmt: i64 = 0,             // statements in the request
    tries: i64 = 0,
    next: i64 = -1,             // the next request waiting on the same Postgres connection
    hash_at: i64 = 0,           // where in msg the bcrypt hash goes
    inb: Bytes = Bytes { .. },  // received, not yet answered
    outb: Bytes = Bytes { .. }, // answered, not yet sent
    ctx: Run = Run { .. },      // the token or password a request keeps while it waits
    ctx_len: i64 = 0,
    msg: Run = Run { .. },      // the request's Bind/Execute messages, kept to send again
    msg_len: i64 = 0,
}

reusable var conns: Conn[>..] = [];
var serial: u32 = 0;

struct Pgc {
    fd: i32 = -1,
    out: Bytes = Bytes { .. },
    inb: Bytes = Bytes { .. },
    head: i64 = -1,             // requests sent and not answered, oldest first
    tail: i64 = -1,
    open: i64 = 0,              // requests appended since the last Sync
    waiting: i64 = 0,
    scan: i64 = 0,              // where in inb the search for the next ReadyForQuery goes on
}

var pgs: Pgc[>..] = [];
var writer = 0;
var epfd: i32 = -1;
var reply_read: i32 = -1;
var reply_write: i32 = -1;
var now_ms = 0;
var ready: u64[>..] = [];       // connections with input left once their answer went out
var scratch: u8[>..] = [];      // the receive buffer

let GROUP_MAX = 64;
let RECV_SIZE = 65536;
let SYNC_MSG: u8[5] = ['S', 0, 0, 0, 4];

fn tag_of(slot: i64) -> u64 { (slot as u64) | (conns[slot].gen as u64) << 32 }

fn ev_data(kind: u64, slot: i64) -> u64 {
    kind << 56 | ((conns[slot].gen as u64) & 0xffffff) << 32 | (slot as u64)
}

fn log(s: const u8[:]) { sys_write(2, str("conduit: ", s, "\n")); }

// ---------- connections ----------

fn free_conn(slot: i64) {
    let c .= conns[slot];
    release(c.inb.run); release(c.outb.run); release(c.ctx); release(c.msg);
    c = Conn { .. };
    conns.free(slot);
}

fn close_conn(slot: i64) {
    let c .= conns[slot];
    if c.fd >= 0 { sys_close(c.fd); c.fd = -1; }
    if c.busy { c.dead = true; return; }
    free_conn(slot);
}

fn gone(slot: i64) -> bool { conns[slot].fd < 0 }

fn accept_all(lfd: i32) {
    loop {
        let fd = sys_accept4(lfd, 0, 0, SOCK_NONBLOCK | SOCK_CLOEXEC);
        if fd < 0 { return; }
        set_int_option(fd, IPPROTO_TCP, TCP_NODELAY, 1);
        serial += 1;
        let slot = conns.alloc_index(Conn { fd: fd, gen: serial, .. });
        if !epoll_add(epfd, fd, EPOLLIN | EPOLLOUT | EPOLLRDHUP | EPOLLET, ev_data(KIND_CLIENT, slot)) {
            sys_close(fd);
            conns[slot].fd = -1;
            free_conn(slot);
        }
    }
}

// Sends the response, or what the socket won't take now into the connection's output.
fn send_res(slot: i64, out: const u8[:]) {
    let c .= conns[slot];
    if c.fd < 0 { return; }
    if pending(c.outb) > 0 { put(c.outb, out); return; }
    var done = 0;
    while done < out.len {
        let n = sys_send(c.fd, out[done..], MSG_NOSIGNAL);
        if n <= 0 { break; }
        done += n;
    }
    if done < out.len { put(c.outb, out[done..]); }
}

// The response goes out, and the connection takes its next request.
fn reply(slot: i64, out: const u8[:]) {
    send_res(slot, out);
    let c .= conns[slot];
    let was_busy = c.busy;
    c.busy = false;
    c.kind = 0;
    c.ctx_len = 0;
    if c.close_after {
        if pending(c.outb) == 0 { close_conn(slot); }
        return;
    }
    if was_busy && pending(c.inb) > 0 { ready.push(tag_of(slot)); }
}

fn fail(slot: i64, status: i64, message: const u8[:]) {
    var out: u8[>..] = [];
    error_response(out, status, message, conns[slot].close_after);
    reply(slot, out);
}

fn flush_out(slot: i64) {
    let c .= conns[slot];
    while pending(c.outb) > 0 {
        let n = sys_send(c.fd, waiting(c.outb), MSG_NOSIGNAL);
        if n <= 0 { return; }
        consume(c.outb, n);
    }
    trim(c.outb, 0);
    if c.close_after && !c.busy { close_conn(slot); }
}

// Answers the whole requests at the start of data, in order, until one has to wait; returns the bytes used.
fn serve(slot: i64, data: const u8[:]) -> i64 {
    var used = 0;
    while !gone(slot) && !conns[slot].busy {
        var r, st = parse_request(data[used..]);
        if st == NEED_MORE { break; }
        if st != PARSED {
            conns[slot].close_after = true;
            fail(slot, st, if st == 413 { "request body too large" } else if st == 431 { "request header too large" } else { "bad request" });
            return data.len;
        }
        used += r.size;
        conns[slot].close_after = r.close;
        handle(slot, r);
    }
    return used;
}

fn on_input(slot: i64, data: const u8[:]) {
    let c .= conns[slot];
    if c.busy || pending(c.inb) > 0 {
        put(c.inb, data);
        if !c.busy { drain(slot); }
        return;
    }
    let used = serve(slot, data);
    if !gone(slot) && used < data.len { put(conns[slot].inb, data[used..]); }
}

fn drain(slot: i64) {
    let used = serve(slot, waiting(conns[slot].inb));
    if gone(slot) { return; }
    consume(conns[slot].inb, used);
    trim(conns[slot].inb, 0);
}

fn client_event(slot: i64, events: u32) {
    if events & (EPOLLIN | EPOLLRDHUP | EPOLLHUP | EPOLLERR) != 0 {
        loop {
            let n = sys_recv(conns[slot].fd, scratch, 0);
            if n == 0 { close_conn(slot); return; }
            if n < 0 { break; }
            on_input(slot, scratch[..n]);
            if gone(slot) { return; }
            if n < scratch.len { break; }
        }
        if events & (EPOLLHUP | EPOLLERR | EPOLLRDHUP) != 0 { close_conn(slot); return; }
    }
    if events & EPOLLOUT != 0 && pending(conns[slot].outb) > 0 { flush_out(slot); }
}

// ---------- Postgres ----------

fn begin(slot: i64, kind: i64, viewer: i64) {
    let c .= conns[slot];
    c.kind = kind;
    c.viewer = viewer;
    c.nstmt = 0;
    c.tries = 0;
}

fn stmt(slot: i64, m: u8[>..]&, st: i64, nparams: i64) -> i64 {
    conns[slot].nstmt += 1;
    return bind_begin(m, st, nparams);
}

fn enqueue(p: i64, slot: i64) {
    let c .= conns[slot];
    c.next = -1;
    c.group_end = false;
    if pgs[p].tail >= 0 { conns[pgs[p].tail].next = slot; } else { pgs[p].head = slot; }
    pgs[p].tail = slot;
}

fn close_group(p: i64) {
    put(pgs[p].out, SYNC_MSG);
    conns[pgs[p].tail].group_end = true;
    pgs[p].open = 0;
}

// Sends the connection's kept request (again).
fn send_kept(slot: i64) {
    let c .= conns[slot];
    var p = writer;
    if !c.write {
        p = 0;
        for i in 1..readers { if pgs[i].waiting < pgs[p].waiting { p = i; } }
    }
    put(pgs[p].out, view(c.msg)[..c.msg_len]);
    enqueue(p, slot);
    pgs[p].waiting += 1;
    pgs[p].open += 1;
    if pgs[p].open >= GROUP_MAX { close_group(p); }
    c.busy = true;
}

// Keeps the request's messages with the connection, to send (again).
fn keep(slot: i64, m: const u8[:]) {
    let c .= conns[slot];
    if c.msg.cap < m.len { release(c.msg); c.msg = run_alloc(max(m.len, 256)); }
    let v = view(c.msg);
    for b, i in m { v[i] = b; }
    c.msg_len = m.len;
}

fn submit(slot: i64, m: const u8[:], write: bool) {
    keep(slot, m);
    conns[slot].write = write;
    send_kept(slot);
}

fn set_ctx(slot: i64, data: const u8[:]) {
    let c .= conns[slot];
    if c.ctx.cap < data.len { release(c.ctx); c.ctx = run_alloc(max(data.len, 128)); }
    let v = view(c.ctx);
    for b, i in data { v[i] = b; }
    c.ctx_len = data.len;
}

fn ctx(slot: i64) -> u8[:] { view(conns[slot].ctx)[..conns[slot].ctx_len] }

fn pg_readable(p: i64) {
    loop {
        let room = spare(pgs[p].inb, RECV_SIZE);
        let n = sys_recv(pgs[p].fd, room, MSG_DONTWAIT);
        if n == 0 { log("postgres closed the connection"); exit(1); }
        if n < 0 { break; }
        grew(pgs[p].inb, n);
        if n < RECV_SIZE { break; }
    }
    loop {
        let w = waiting(pgs[p].inb);
        var at = pgs[p].scan;
        var z = -1;
        loop {
            let n = msg_len(w[at..]);
            if n == 0 { break; }
            if w[at] == 'Z' { z = at + n; break; }
            at += n;
        }
        if z < 0 { pgs[p].scan = at; break; }
        group_done(p, w[..z]);
        consume(pgs[p].inb, z);
        pgs[p].scan = 0;
    }
}

fn pg_flush() {
    for p in pgs.len {
        if pgs[p].open > 0 { close_group(p); }
        while pending(pgs[p].out) > 0 {
            let n = sys_send(pgs[p].fd, waiting(pgs[p].out), MSG_DONTWAIT | MSG_NOSIGNAL);
            if n <= 0 { break; }
            consume(pgs[p].out, n);
        }
    }
}

// One Sync group's answer, up to and including its ReadyForQuery.
fn group_done(p: i64, data: const u8[:]) {
    var members: i64[..64] = [];
    loop {
        let s = pgs[p].head;
        assert(s >= 0);
        pgs[p].head = conns[s].next;
        members.push(s);
        if conns[s].group_end { break; }
    }
    if pgs[p].head < 0 { pgs[p].tail = -1; }
    pgs[p].waiting -= members.len;

    // An error rolls the whole group back: find whose statement failed.
    var at = 0;
    var k = 0;
    var done = 0;
    var failed = -1;
    var err_at = 0;
    while at < data.len {
        let n = read32(data, at + 1) + 1;
        let t = data[at];
        if t == 'C' {
            done += 1;
            if done == conns[members[k]].nstmt { k += 1; done = 0; }
        } else if t == 'E' {
            failed = k;
            err_at = at;
            break;
        }
        at += n;
    }
    if failed >= 0 {
        let code, text = error_fields(data[err_at..err_at + read32(data, err_at + 1) + 1]);
        for s, i in members {
            if conns[s].dead { free_conn(s); continue; }
            if i == failed { pend_failed(s, code, text); } else { send_kept(s); }
        }
        return;
    }

    at = 0;
    k = 0;
    done = 0;
    var start = 0;
    while at < data.len && k < members.len {
        let n = read32(data, at + 1) + 1;
        if data[at] == 'C' {
            done += 1;
            let s = members[k];
            if done == conns[s].nstmt {
                if conns[s].dead { free_conn(s); } else { answer(s, data[start..at]); }
                k += 1;
                done = 0;
            }
            start = at + n;
        }
        at += n;
    }
}

fn pend_failed(slot: i64, code: const u8[:], text: const u8[:]) {
    let c .= conns[slot];
    if (code == "40P01" || code == "40001") && c.tries < 5 {
        c.tries += 1;
        send_kept(slot);
    } else if code == "23505" {
        fail(slot, 422, "username or email is already taken");
    } else if code == "55P03" || code == "57014" || code == "40P01" || code == "40001" {
        fail(slot, 503, "the database is busy");
    } else {
        log(str("statement failed: ", code, " ", text));
        fail(slot, 500, "internal error");
    }
}

// ---------- bcrypt ----------

fn hash_job(slot: i64, verify: bool, password: const u8[:], hash: const u8[:]) {
    var j = BJob { tag: tag_of(slot), reply_fd: reply_write, verify: verify, password: "", hash: "" };
    j.password.append(password);
    j.hash.append(hash[..min(hash.len, 60)]);
    conns[slot].busy = true;
    qput(j);
}

fn replies() {
    var b: u8[4320] = [0; 4320];             // 60 replies
    loop {
        let n = sys_read(reply_read, b);
        if n <= 0 { return; }
        var at = 0;
        while at + REPLY_SIZE <= n {
            let r = parse_reply(b[at..at + REPLY_SIZE]);
            at += REPLY_SIZE;
            let slot = (r.tag & 0xffffffff) as! i64;
            if conns[slot].gen != (r.tag >> 32) as! u32 { continue; }
            if conns[slot].dead { free_conn(slot); continue; }
            hashed(slot, r);
        }
        if n < b.len { return; }
    }
}

fn hashed(slot: i64, r: BReply) {
    let c .= conns[slot];
    if c.kind == K_LOGIN_CHECK {
        if !r.ok { fail(slot, 401, "invalid email or password"); return; }
        reply(slot, view(c.msg)[..c.msg_len]);
        return;
    }
    if !r.ok || r.hash.len != 60 { fail(slot, 500, "internal error"); return; }
    let v = view(c.msg);
    for b, i in r.hash { v[c.hash_at + i] = b; }
    c.kind = if c.kind == K_REGISTER_HASH { K_REGISTER } else { K_UPDATE_USER };
    send_kept(slot);
}

// ---------- the loop ----------

thread_fn worker(index: i64) {
    for i in RECV_SIZE { scratch.push(0); }
    epfd = sys_epoll_create1(O_CLOEXEC);
    var fds: i32[2] = [-1, -1];
    if sys_pipe2(fds, O_NONBLOCK | O_CLOEXEC) != 0 { log("pipe2 failed"); exit(1); }
    reply_read = fds[0];
    reply_write = fds[1];

    var stmts: u8[>..] = [];
    prepare_all(stmts);
    let common = "jit\x00off\x00plan_cache_mode\x00force_generic_plan\x00max_parallel_workers_per_gather\x000\x00statement_timeout\x0030s\x00";
    for i in readers + 1 {
        let settings = if i < readers { str(common) } else { str(common, "lock_timeout\x002s\x00") };
        var fd: i32 = -1;
        for attempt in 120 {
            fd = pg_connect(pg_addr, pg_port, pg_user, pg_password, pg_db, settings, stmts);
            if fd >= 0 { break; }
            nap_ms(500);
        }
        if fd < 0 { log("could not reach postgres"); exit(1); }
        pgs.push(Pgc { fd: fd, .. });
        epoll_add(epfd, fd, EPOLLIN | EPOLLOUT | EPOLLET, KIND_PG << 56 | (i as u64));
    }
    writer = readers;

    let lfd = listen_on(listen_addr, listen_port);
    if lfd < 0 { log("could not listen"); exit(1); }
    epoll_add(epfd, lfd, EPOLLIN, KIND_LISTENER << 56);
    epoll_add(epfd, reply_read, EPOLLIN, KIND_PIPE << 56);

    var evs: EpollEvent[256] = [EpollEvent { events: 0, data: 0 }; 256];
    loop {
        let n = sys_epoll_wait(epfd, evs, -1);
        now_ms = wall_ns() / 1000000;
        for i in n as i64 {
            let e = evs[i];
            let kind = e.data >> 56;
            if kind == KIND_CLIENT {
                let slot = (e.data & 0xffffffff) as! i64;
                if slot < conns.len && !gone(slot) && ((conns[slot].gen as u64) & 0xffffff) == (e.data >> 32 & 0xffffff) {
                    client_event(slot, e.events);
                }
            } else if kind == KIND_PG {
                pg_readable((e.data & 0xffffffff) as! i64);
            } else if kind == KIND_PIPE {
                replies();
            } else {
                accept_all(lfd);
            }
        }
        for t in ready {
            let slot = (t & 0xffffffff) as! i64;
            if !gone(slot) && conns[slot].gen == (t >> 32) as! u32 && !conns[slot].busy { drain(slot); }
        }
        ready.clear();
        pg_flush();
    }
}
