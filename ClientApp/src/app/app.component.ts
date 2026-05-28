import { Component, HostListener, signal } from '@angular/core';

interface PhotinoExternal {
  sendMessage: (message: string) => void;
  receiveMessage: (callback: (message: string) => void) => void;
}

declare global {
  interface External extends PhotinoExternal {}
}

@Component({
  selector: 'app-root',
  standalone: true,
  templateUrl: './app.component.html'
})
export class AppComponent {
  displayText = signal('Hello World');
  awaitingResponse = signal(false);

  constructor() {
    window.external.receiveMessage((message: string) => {
      if (message !== '') {
        this.displayText.set(message);
      }
      this.awaitingResponse.set(false);
    });
  }

  @HostListener('document:keydown', ['$event'])
  onKeydown(event: KeyboardEvent): void {
    const isCtrlO = (event.ctrlKey || event.metaKey) && event.key === 'o';
    if (!isCtrlO) {
      return;
    }

    event.preventDefault();

    if (!this.awaitingResponse()) {
      window.external.sendMessage('open-file');
      this.awaitingResponse.set(true);
    }
  }
}
