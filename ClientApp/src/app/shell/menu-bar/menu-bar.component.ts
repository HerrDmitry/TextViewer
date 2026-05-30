import { Component, ElementRef, inject, signal } from '@angular/core';
import { ShellStateService } from '../shell-state.service';

@Component({
  selector: 'app-menu-bar',
  standalone: true,
  templateUrl: './menu-bar.component.html',
  styleUrl: './menu-bar.component.css'
})
export class MenuBarComponent {
  private readonly state = inject(ShellStateService);
  private readonly el = inject(ElementRef);
  readonly isOpenDisabled = this.state.isOpenFilePending;
  menuOpen = signal(false);

  toggleMenu(): void { this.menuOpen.update(v => !v); }
  closeMenu(): void { this.menuOpen.set(false); }

  onOpen(): void {
    this.menuOpen.set(false);
    // Synchronous DOM hide — ensures dropdown gone before native dialog blocks UI thread
    const dropdown = this.el.nativeElement.querySelector('.dropdown') as HTMLElement | null;
    if (dropdown) dropdown.style.display = 'none';
    this.state.triggerOpenFile();
  }

  onExit(): void {
    this.state.sendExit();
  }
}
