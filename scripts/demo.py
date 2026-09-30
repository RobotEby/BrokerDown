#!/usr/bin/env python3
"""Executable assertions for the Docker Compose lab; Python standard library only."""
import argparse
import concurrent.futures
import datetime
import json
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
ORDERS, PAYMENTS, WORKER, PROMETHEUS = (f'http://localhost:{p}' for p in (5001, 5002, 5003, 9090))


class HttpFailure(RuntimeError):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def http(base, path, data=None, method=None):
    request = urllib.request.Request(base + path, None if data is None else json.dumps(data).encode(),
                                     {'Content-Type': 'application/json'}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            body = response.read().decode()
            return response.status, json.loads(body) if body and body[0] in '{[' else body
    except urllib.error.HTTPError as error:
        raise HttpFailure(error.code, f'{request.get_method()} {path}: HTTP {error.code}: {error.read().decode()}') from error


def eventually(condition, timeout, description, diagnostic=None):
    until, last = time.monotonic() + timeout, None
    while time.monotonic() < until:
        try:
            result = condition()
            if result:
                return result
        except (urllib.error.URLError, TimeoutError) as error:
            last = str(error)
        except HttpFailure as error:
            if error.code not in (502, 503):
                raise
            last = str(error)
        time.sleep(0.25)
    raise AssertionError(f'Timeout waiting for {description}; last error: {last}; last state: {diagnostic() if diagnostic else "condition false"}')


def compose(*args):
    return subprocess.run(['docker', 'compose', *args], cwd=ROOT, check=True, text=True, capture_output=True).stdout


class Demo:
    def __init__(self):
        self.orders = []
        self.results = []

    def create(self):
        code, order = http(ORDERS, '/orders', {'customerId': str(uuid.uuid4()), 'amount': 149.90})
        assert code == 202 and order['status'] == 'Pending', order
        order_id = str(uuid.UUID(order['id']))
        self.orders.append(order_id)
        return order_id

    def paid(self, order_id, timeout=30):
        order = None
        def completed():
            nonlocal order
            order = http(ORDERS, f'/orders/{order_id}')[1]
            assert order['status'] != 'PaymentFailed', order
            return order['status'] == 'Paid'
        eventually(completed, timeout, f'order {order_id} paid', lambda: order)
        payment = http(PAYMENTS, f'/payments/{order_id}')[1]
        assert payment['status'] == 'Approved' and payment['amount'] == 149.90, payment
        return payment

    def batch(self, count=20):
        with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
            return list(executor.map(lambda _: self.paid(self.create()), range(count)))

    def available(self):
        for base in (ORDERS, PAYMENTS, WORKER):
            eventually(lambda: http(base, '/health/ready')[0] == 200, 90, base + ' ready')

    def normal(self):
        start = time.monotonic()
        payments = self.batch()
        assert all(p['gateway'] == 'primary' for p in payments), payments
        self.results.append({'scenario': 'normal', 'orders': len(payments), 'seconds': round(time.monotonic() - start, 2)})

    def warm(self):
        print('Waiting for cooldown and fresh, healthy metrics while generating traffic...', flush=True)
        until = time.monotonic() + 150
        while time.monotonic() < until:
            snapshot = http(WORKER, '/chaos')[1]
            assert snapshot['enabled'] and not snapshot['killSwitch'], 'Restart payments-api and chaos-worker to reset the kill switch'
            metrics = snapshot.get('metrics') or {}
            cooldown = datetime.datetime.fromisoformat(snapshot['cooldownUntil'].replace('Z', '+00:00')).timestamp()
            if metrics.get('healthy') and metrics.get('enoughTraffic') and cooldown <= time.time():
                return
            self.paid(self.create())
            time.sleep(0.5)
        raise AssertionError(f'Worker never received safe metrics: {snapshot}')

    def experiment(self, fault, abort=False):
        self.warm()
        start = time.monotonic()
        code, requested = http(WORKER, '/chaos/experiments', {'fault': fault, 'durationSeconds': 30, 'latencyMilliseconds': 2000})
        assert code == 202, requested
        experiment_id = requested['experimentId']
        def active():
            run = http(WORKER, '/chaos')[1]['current']
            assert run['experimentId'] == experiment_id, run
            assert run['status'] not in ('rejected', 'aborted', 'expired'), run
            return run['status'] == 'active'
        eventually(active, 40, 'target acknowledgement')
        payments = self.batch()
        fallback = sum(p['gateway'] == 'fallback' for p in payments)
        assert fallback > 0, 'Fault did not exercise fallback'
        if abort:
            http(WORKER, '/chaos/abort', {}, 'POST')
        expected = 'aborted' if abort else 'expired'
        def ended():
            run = http(WORKER, '/chaos')[1]['current']
            if run['status'] in ('aborted', 'expired', 'rejected'):
                assert run['status'] == expected, run
                return run
            return False
        eventually(ended, 45, expected)
        # A circuit may remain open until its next half-open probe.
        eventually(lambda: self.paid(self.create())['gateway'] == 'primary', 30, 'primary gateway recovery')
        self.results.append({'scenario': fault.lower(), 'orders': len(payments), 'fallback': fallback,
                             'ended': expected, 'seconds': round(time.monotonic() - start, 2)})

    def rabbitmq(self):
        start = time.monotonic()
        try:
            compose('stop', 'rabbitmq')
            order_id = self.create()
            assert http(ORDERS, f'/orders/{order_id}')[1]['status'] == 'Pending'
        finally:
            compose('start', 'rabbitmq')
        recovered = time.monotonic()
        payment = self.paid(order_id, 120)
        self.results.append({'scenario': 'rabbitmq', 'orderId': order_id, 'gateway': payment['gateway'],
                             'restart_to_paid_seconds': round(time.monotonic() - recovered, 2),
                             'seconds': round(time.monotonic() - start, 2)})

    def verify_database(self):
        ids = ','.join("'" + str(uuid.UUID(order_id)) + "'" for order_id in self.orders)
        count = len(self.orders)
        query = f"""USE PaymentsDb;
IF (SELECT COUNT(*) FROM Payments WHERE OrderId IN ({ids})) <> {count} THROW 51000, 'Missing or duplicate payment', 1;
IF (SELECT COUNT(*) FROM SimulatedCharges WHERE OrderId IN ({ids})) <> {count} THROW 51001, 'Missing or duplicate charge', 1;
IF EXISTS (SELECT 1 FROM Payments p JOIN SimulatedCharges c ON p.OrderId=c.OrderId WHERE p.OrderId IN ({ids}) AND (p.Gateway<>c.Gateway OR p.Amount<>c.Amount OR c.Success<>1)) THROW 51002, 'Payment does not match charge', 1;
SELECT 'Verified {count} payments and {count} unique charges';"""
        print(compose('exec', '-T', 'sqlserver', 'sh', '-c',
                      'exec /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b -Q "$1"', '--', query).strip(), flush=True)

    def verify_metrics(self):
        query = 'sum(chaoslab_resilience_events_total{event="fallback"})'
        def observed():
            response = http(PROMETHEUS, '/api/v1/query?' + urllib.parse.urlencode({'query': query}))[1]
            values = response['data']['result']
            return values and float(values[0]['value'][1]) > 0
        eventually(observed, 20, 'fallback metric in Prometheus')

    def kill_switch(self):
        http(WORKER, '/chaos/kill-switch', {}, 'POST')
        assert http(WORKER, '/chaos')[1]['killSwitch']
        try:
            http(WORKER, '/chaos/experiments', {'fault': 'Unavailable'})
        except HttpFailure as error:
            assert error.code == 409, error
        else:
            raise AssertionError('Kill switch accepted an experiment')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--scenario', choices=['normal', 'latency', 'unavailable', 'rabbitmq', 'all'], default='all')
    parser.add_argument('--output', type=pathlib.Path)
    args = parser.parse_args()
    demo = Demo()
    demo.available()
    if args.scenario in ('normal', 'all'):
        demo.normal()
    if args.scenario in ('unavailable', 'all'):
        demo.experiment('Unavailable', abort=True)
    if args.scenario in ('latency', 'all'):
        demo.experiment('Latency')
    if args.scenario in ('rabbitmq', 'all'):
        demo.rabbitmq()
    demo.verify_database()
    if args.scenario in ('latency', 'unavailable', 'all'):
        demo.verify_metrics()
    if args.scenario == 'all':
        demo.kill_switch()
    result = json.dumps({'orders_verified': len(demo.orders), 'scenarios': demo.results}, indent=2)
    print(result, flush=True)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(result + '\n')


if __name__ == '__main__':
    try:
        main()
    except BaseException:
        try:
            http(WORKER, '/chaos/abort', {}, 'POST')
        except Exception as cleanup_error:
            print(f'Could not confirm abort; target TTL still applies: {cleanup_error}', file=sys.stderr)
        raise
