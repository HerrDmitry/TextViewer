import { Component, HostListener, signal, inject, OnDestroy } from '@angular/core';
import { MessageBusClient } from './services/message-bus-client.service';
import { InboundMessage, SubscriptionHandle } from './services/message-bus.types';

@Component({
  selector: 'app-root',
  standalone: true,
  templateUrl: './app.component.html'
})
export class AppComponent implements OnDestroy {
  displayText = signal('Hello World');

  private readonly messageBus = inject(MessageBusClient);
  private pendingCorrelationId: string | null = null;
  private subscription: SubscriptionHandle;

  constructor() {
    this.subscription = this.messageBus.subscribe('open-file', (msg: InboundMessage) => {
      if (msg.payload !== '') {
        this.displayText.set(msg.payload);
      }
      this.pendingCorrelationId = null;
    });
  }

  ngOnDestroy(): void {
    this.subscription.unsubscribe();
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    const isCtrlO = (event.ctrlKey || event.metaKey) && event.key === 'o';
    if (!isCtrlO) return;
    event.preventDefault();

    // Guard: don't send while awaiting response
    if (this.pendingCorrelationId !== null) return;

    this.pendingCorrelationId = this.messageBus.send('open-file');
  }
}
